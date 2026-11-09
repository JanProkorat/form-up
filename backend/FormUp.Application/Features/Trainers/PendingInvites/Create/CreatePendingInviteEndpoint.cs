using System.Security.Claims;
using System.Security.Cryptography;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Trainers.PendingInvites.Create;

/// <summary>
/// Endpoint for creating a pending client invitation.
/// Creates both a PendingInvite record and an InvitationToken, then sends the invitation email.
/// Also creates an in-app notification and sends a real-time event if the client already has an
/// account. If that account belongs to a CLIENT and its email is already verified, immediately
/// seeds the professional-client conversation with an Invited cooperation event — see the
/// maintainer ruling in <see cref="HandleAsync"/>. For an unverified or nonexistent client
/// account, the identical rows are written later, once the email is verified, by
/// <c>VerifyEmailEndpoint</c> via <see cref="IPendingInviteConversationSeeder"/>.
/// An email that belongs to a coaching professional gets the same success response and a stored
/// <see cref="PendingInvite"/> row that behaves identically for the inviter, but no token, email,
/// notification, realtime event or conversation — so the response never reveals that the address
/// is a coach account.
/// </summary>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
public class CreatePendingInviteEndpoint(
    IApplicationDbContext db,
    IBackgroundEmailQueue emailQueue,
    INotificationService notificationService,
    IRealtimeNotifier notifier,
    IConversationSeedService conversationSeedService,
    UserManager<ApplicationUser> userManager,
    EntitlementService entitlementService,
    ILogger<CreatePendingInviteEndpoint> logger,
    TimeProvider timeProvider) : Endpoint<CreatePendingInviteRequest, CreatePendingInviteResponse>
{
    /// <summary>
    /// Maximum number of outstanding (unaccepted) pending invites a single professional may
    /// hold at once. Bounds the standing fan-out an abusive account can build up even when
    /// paced below the <see cref="AppPolicies.PendingInviteRateLimit"/> window — 200 comfortably
    /// exceeds any real professional's prospective-client roster (claude-security F8).
    /// </summary>
    private const int MaxOutstandingInvitesPerProfessional = 200;

    /// <inheritdoc />
    public override void Configure()
    {
        Post("/trainer/pending-invites");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist, AppRoles.Admin);
        Options(x => x.RequireRateLimiting(AppPolicies.PendingInviteRateLimit));
        Summary(s =>
        {
            s.Summary = "Create a pending invitation";
            s.Description = "Creates a pending invitation for a client, sends an invitation email with a one-time token valid for 7 days.";
            s.Responses[StatusCodes.Status400BadRequest] =
                "Requested scope exceeds the caller's held roles, a discount was sent without a package, or a fixed discount is in another currency than the package (CURRENCY_MISMATCH).";
            s.Responses[StatusCodes.Status403Forbidden] =
                "The coach role was removed, or the subscription does not include payments (PAYMENTS_NOT_ENTITLED).";
            s.Responses[StatusCodes.Status404NotFound] = "The package or discount is unknown or belongs to another coach.";
            s.Responses[StatusCodes.Status409Conflict] =
                "A duplicate invite exists, or the package is archived (PACKAGE_ARCHIVED), the discount is archived (DISCOUNT_ARCHIVED) or does not apply to the package (DISCOUNT_PACKAGE_INVALID).";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CreatePendingInviteRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var professionalProfile = await db.ProfessionalProfiles
            .FirstOrDefaultAsync(tp => tp.UserId == Guid.Parse(userId), ct);

        if (professionalProfile is null)
        {
            ThrowError("Professional profile not found. Please complete your profile setup first.");
            return;
        }

        // A requested scope narrows the eventual link's CanView* flags below the full
        // set implied by the inviting professional's held roles — it must never widen
        // them. Reject (400), don't clamp: a request for a domain the professional
        // doesn't hold is a caller error, not something to silently downgrade.
        if (req.RequestedScope == LinkCapabilityScope.NutritionOnly && !User.IsInRole(AppRoles.Nutritionist))
        {
            this.ThrowErrorWithCode(
                ErrorCodes.RequestedScopeExceedsHeldRoles,
                "Requested scope exceeds the caller's held roles.");
            return;
        }

        if (req.RequestedScope == LinkCapabilityScope.TrainingOnly && !User.IsInRole(AppRoles.Trainer))
        {
            this.ThrowErrorWithCode(
                ErrorCodes.RequestedScopeExceedsHeldRoles,
                "Requested scope exceeds the caller's held roles.");
            return;
        }

        // A removed coach role is read-only: an invite may not grant it. A missing scope grants
        // only the disciplines whose role is still active.
        var heldCoachRoles = new[] { AppRoles.Trainer, AppRoles.Nutritionist }.Where(User.IsInRole).ToList();

        if (!CoachRoleStatus.TryResolveLinkGrant(
                req.RequestedScope, heldCoachRoles, professionalProfile,
                out var wantsNutritionPlans, out var wantsTrainingPlans))
        {
            await this.SendErrorWithCodeAsync(
                ErrorCodes.CoachRoleRemoved,
                "This coach role was removed; the account is read-only for it.",
                StatusCodes.Status403Forbidden,
                ct);
            return;
        }

        // An email that belongs to an account holding a coaching professional role (Trainer or
        // Nutritionist — even with Client as a second role) is not rejected: rejecting would let
        // the caller probe which addresses are coach accounts. Such an invite is stored as a
        // silent row instead — see the class remarks. The same lookup also answers the
        // profession-slot question used further down.
        var inviteeSlots = await InviteeProfessionSlotCheck.CheckAsync(
            db, userManager, req.Email, professionalProfile.Id,
            wantsNutritionPlans, wantsTrainingPlans, ct);

        var isProfessionalInvitee = inviteeSlots.IsCoachAccount;
        var inviteeClientProfileId = inviteeSlots.ClientProfileId;

        // Reject a duplicate pending invite for the same professional and email — repeatedly
        // re-inviting the same target is the abuse shape, not a legitimate workflow need (a
        // professional who wants to resend can delete the existing invite first). An invite past
        // its validity window no longer counts here or toward the cap below.
        var validSince = PendingInviteStatusResolver.ValidSince(timeProvider.GetUtcNow().UtcDateTime);
        var reqEmailLowerForDuplicateCheck = req.Email.ToLower();
        var hasDuplicate = await db.PendingInvites
            .AsNoTracking()
            .AnyAsync(pi => pi.ProfessionalProfileId == professionalProfile.Id
                         && pi.Email.ToLower() == reqEmailLowerForDuplicateCheck
                         && pi.Status == PendingInviteStatus.Pending
                         && pi.SentAt >= validSince, ct);

        if (hasDuplicate)
        {
            await this.SendProblemAsync(409, ErrorCodes.DuplicatePendingInvite,
                "An unaccepted invite already exists for this email.", ct);
            return;
        }

        // Cap outstanding (unaccepted) invites per professional — bounds the standing fan-out
        // an abusive account can build up even when paced below the rate-limit window.
        var outstandingCount = await db.PendingInvites
            .AsNoTracking()
            .CountAsync(pi => pi.ProfessionalProfileId == professionalProfile.Id
                              && pi.Status == PendingInviteStatus.Pending
                              && pi.SentAt >= validSince, ct);

        if (outstandingCount >= MaxOutstandingInvitesPerProfessional)
        {
            await this.SendProblemAsync(429, ErrorCodes.TooManyPendingInvites,
                "Maximum number of outstanding pending invites reached.", ct);
            return;
        }

        // Backstop for the live slot check the invite drawer runs while the coach types
        // (GET /trainer/pending-invites/slot-check, #1148): refuse to invite a client who already
        // has an active coach in the profession this invite would grant. The authoritative check
        // stays at accept time — that one runs inside the row lock (#1009) and is what actually
        // protects the invariant, since the client's links can change between invite and accept.
        //
        // Note this necessarily tells the inviting professional that the address they typed
        // belongs to an account whose slot is taken. That is a deliberate trade-off for the
        // earlier feedback, and it is narrow: only for an email they already knew, and it
        // reveals that the slot is occupied, never by whom. The shared helper reports no taken
        // slot for coach accounts or for invitees without a client profile, so neither is refused.
        if (inviteeSlots.AnySlotTaken)
        {
            this.ThrowErrorWithCode(
                ErrorCodes.ProfessionAlreadyOccupied,
                "The client already has an active professional occupying this profession slot.");
            return;
        }

        // The package and discount are checked with the rules the coach's own client terms follow, before
        // anything is saved or sent.
        var (package, discount) = await LoadPackageTermsOrRespondAsync(req, professionalProfile.Id, ct);

        if (req.PackageId is not null && package is null)
        {
            return;
        }

        // Resolve optional questionnaire
        long? questionnaireId = null;
        if (req.QuestionnairePublicId.HasValue)
        {
            var questionnaire = await db.Questionnaires
                .FirstOrDefaultAsync(q => q.PublicId == req.QuestionnairePublicId.Value
                    && q.ProfessionalId == Guid.Parse(userId), ct);

            if (questionnaire?.Type == QuestionnaireType.CheckIn)
            {
                this.ThrowErrorWithCode(ErrorCodes.CheckInFormNotSendable, "Check-in forms cannot be sent as a questionnaire.");
                return;
            }

            questionnaireId = questionnaire?.Id;
        }

        // Create the PendingInvite record
        var pendingInvite = new PendingInvite
        {
            ProfessionalProfileId = professionalProfile.Id,
            Email = req.Email,
            Message = req.Message,
            SentAt = DateTime.UtcNow,
            QuestionnaireId = questionnaireId,
            SkipOnboardingForm = req.SkipOnboardingForm,
            RequestedScope = req.RequestedScope,
            CoachPackageId = package?.Id,
            CoachDiscountId = discount?.Id
        };

        db.PendingInvites.Add(pendingInvite);

        if (isProfessionalInvitee)
        {
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Silent pending invitation stored from professional {ProfessionalId} to a coaching account",
                professionalProfile.PublicId);

            await Send.ResponseAsync(BuildResponse(pendingInvite, req.QuestionnairePublicId), cancellation: ct);
            return;
        }

        // Create the InvitationToken so the accept flow still works. Stamped with the
        // same requested scope so AcceptInvitationEndpoint (token-based accept) honors
        // the identical choice as AcceptClientInviteEndpoint (in-app accept via the
        // PendingInvite id) — whichever path the client uses to accept.
        var tokenValue = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

        var invitation = new InvitationToken
        {
            ProfessionalProfileId = professionalProfile.Id,
            Email = req.Email,
            Token = tokenValue,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            RequestedScope = req.RequestedScope
        };

        db.InvitationTokens.Add(invitation);
        await db.SaveChangesAsync(ct);

        // Send invitation email
        var trainerUser = await db.Users.FirstAsync(u => u.Id == professionalProfile.UserId, ct);
        var trainerName = $"{trainerUser.FirstName} {trainerUser.LastName}";

        var language = HttpContext.Request.Headers.AcceptLanguage.FirstOrDefault() ?? "en";

        // Enqueue the send fire-and-forget (#1109): an SMTP failure in the background worker
        // must never turn a successful invite creation into a 500, or skip the notification,
        // realtime event, and #1100 chat seed below. TryEnqueue is non-blocking; a full or
        // completed queue drops the send and logs rather than falling back to a synchronous
        // send that would reintroduce exactly the failure mode this closes.
        if (!emailQueue.TryEnqueue(new InvitationEmailWorkItem(
                req.Email, trainerName, tokenValue, language, req.Message,
                package is null ? null : new InvitationPackageInfo(package.Name, package.Billing, package.IntervalWeeks),
                ResolveCoachRole(wantsTrainingPlans, wantsNutritionPlans))))
        {
            logger.LogWarning(
                "Background email queue full; dropped invitation email enqueue for {Email}.",
                req.Email);
        }

        // If the invited client already has an account, create an in-app notification + real-time event
        var reqEmailLower = req.Email.ToLower();
        var existingUser = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email!.ToLower() == reqEmailLower, ct);

        if (existingUser is not null)
        {
            // MAINTAINER RULING 2026-09-23 (closed, do not reopen): F8 is reversed. This branch
            // used to defer the conversation seed to acceptance — claude-security's original F8
            // finding was that seeding it here let any professional account drop attacker-written
            // prose straight into an arbitrary stranger's message stream, addressed only by
            // guessing their email. The maintainer's call: for a VERIFIED account, seed the
            // conversation and write the Invited event (plus the message beneath it, if any)
            // immediately below, instead of waiting for accept.
            //
            // Accepted risk: an authenticated professional who already knows a registered,
            // verified email address can write an entry into that account's own message thread
            // before the invitee has agreed to anything. This is bounded, not open-ended — by
            // AppPolicies.PendingInviteRateLimit and by MaxOutstandingInvitesPerProfessional
            // (200) above, both of which cap the fan-out a single abusive account can build.
            // An unverified or nonexistent account gets no thread here — VerifyEmailEndpoint
            // seeds the identical rows once the account is verified (R4).
            //
            // The notification and realtime event below stay: their payload is composed here
            // from the professional's own profile and the invite id, the invitee needs some
            // signal that an invite arrived, and the message they carry is the same text
            // GetPendingInviteEndpoint already returns for the invite itself.
            await notificationService.CreateAsync(
                existingUser.Id,
                NotificationType.InvitationReceived,
                new Dictionary<string, string>
                {
                    ["trainerName"] = trainerName,
                    ["inviteId"] = pendingInvite.PublicId.ToString(),
                },
                ct: ct);

            var senderRole = User.IsInRole(AppRoles.Nutritionist)
                             && !CoachRoleStatus.IsRemoved(professionalProfile, AppRoles.Nutritionist)
                ? "Nutritionist"
                : "Trainer";

            await notifier.NotifyAsync(
                existingUser.Id,
                "invitationreceived",
                new
                {
                    id = pendingInvite.PublicId,
                    trainerId = professionalProfile.PublicId,
                    trainerName,
                    trainerRole = senderRole,
                    trainerCity = professionalProfile.City ?? string.Empty,
                    message = pendingInvite.Message
                },
                ct);

            // Verified CLIENT accounts only (R4), never a self-invite — an unverified
            // invitee gets no thread until VerifyEmailEndpoint seeds the identical rows,
            // and a verified peer professional or the caller's own email gets no thread
            // at all (#1108 review: EmailConfirmed alone let a professional seed a
            // conversation into a peer's or their own message stream).
            if (existingUser.EmailConfirmed
                && inviteeClientProfileId != 0
                && existingUser.Id != professionalProfile.UserId)
            {
                await conversationSeedService.AppendCooperationEventAsync(
                    professionalProfile.UserId,
                    existingUser.Id,
                    professionalProfile.UserId,
                    ChatEventType.Invited,
                    pendingInvite.PublicId,
                    pendingInvite.Message,
                    createConversationIfMissing: true,
                    ct);
            }
        }

        logger.LogInformation(
            "Pending invitation created from professional {ProfessionalId} to {Email}",
            professionalProfile.PublicId, req.Email);

        await Send.ResponseAsync(BuildResponse(pendingInvite, req.QuestionnairePublicId), cancellation: ct);
    }

    /// <summary>
    /// Loads the requested package and discount and checks them against <see cref="PackageTermsRules"/>. Answers
    /// the failure and returns a null package when they cannot be used; returns nulls when no package was asked for.
    /// </summary>
    private async Task<(CoachPackage? Package, CoachDiscount? Discount)> LoadPackageTermsOrRespondAsync(
        CreatePendingInviteRequest req, long professionalProfileId, CancellationToken ct)
    {
        if (req.PackageId is not { } packageId)
        {
            return (null, null);
        }

        var entitlements = await entitlementService.GetEntitlementsAsync(professionalProfileId, ct);

        var package = await db.CoachPackages
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.PublicId == packageId && p.ProfessionalProfileId == professionalProfileId, ct);

        var discount = req.DiscountId is not { } discountId
            ? null
            : await db.CoachDiscounts
                .AsNoTracking()
                .Include(d => d.Packages)
                .FirstOrDefaultAsync(d => d.PublicId == discountId && d.ProfessionalProfileId == professionalProfileId, ct);

        var failure = PackageTermsRules.Check(entitlements.CanUsePayments, package, req.DiscountId is not null, discount);

        switch (failure)
        {
            case PackageTermsFailure.None:
                return (package, discount);
            case PackageTermsFailure.NotEntitled:
                await this.SendProblemAsync(
                    StatusCodes.Status403Forbidden,
                    ErrorCodes.PaymentsNotEntitled,
                    "The current subscription does not include payments.",
                    ct);
                break;
            case PackageTermsFailure.PackageNotFound or PackageTermsFailure.DiscountNotFound:
                await Send.NotFoundAsync(ct);
                break;
            case PackageTermsFailure.PackageArchived:
                await this.SendProblemAsync(StatusCodes.Status409Conflict, ErrorCodes.PackageArchived, "The package is archived.", ct);
                break;
            case PackageTermsFailure.DiscountArchived:
                await this.SendProblemAsync(StatusCodes.Status409Conflict, ErrorCodes.DiscountArchived, "The discount is archived.", ct);
                break;
            case PackageTermsFailure.DiscountPackageInvalid:
                await this.SendProblemAsync(
                    StatusCodes.Status409Conflict,
                    ErrorCodes.DiscountPackageInvalid,
                    "The discount does not apply to the package.",
                    ct);
                break;
            default:
                await this.SendProblemAsync(
                    StatusCodes.Status400BadRequest,
                    ErrorCodes.CurrencyMismatch,
                    "A fixed discount can only be used with a package in its own currency.",
                    ct);
                break;
        }

        return (null, null);
    }

    // The email names the disciplines this invite grants; no grant means no role line.
    private static InvitationCoachRole? ResolveCoachRole(bool wantsTrainingPlans, bool wantsNutritionPlans) =>
        (wantsTrainingPlans, wantsNutritionPlans) switch
        {
            (true, true) => InvitationCoachRole.Both,
            (true, false) => InvitationCoachRole.Trainer,
            (false, true) => InvitationCoachRole.Nutritionist,
            _ => null
        };

    private static CreatePendingInviteResponse BuildResponse(PendingInvite pendingInvite, Guid? questionnairePublicId) =>
        new()
        {
            Id = pendingInvite.Id,
            PublicId = pendingInvite.PublicId,
            Email = pendingInvite.Email,
            SentAt = pendingInvite.SentAt,
            QuestionnairePublicId = questionnairePublicId
        };
}
