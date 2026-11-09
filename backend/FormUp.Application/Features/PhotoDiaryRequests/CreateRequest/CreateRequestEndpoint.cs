using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace FormUp.Application.Features.PhotoDiaryRequests.CreateRequest;

/// <summary>
/// POST /trainer/photo-diary-requests
/// Creates a new photo diary request attached to an existing client link or pending invite.
/// After a successful save, emits a <c>photoDiaryRequested</c> SignalR event to the
/// <b>client</b> group so the client sees the new banner in real time:
/// <list type="bullet">
///   <item>Link-based: notified via <c>link.ClientProfile.UserId</c> (client group).</item>
///   <item>Invite-based + existing user: notified via the user found by the invite e-mail.</item>
///   <item>Invite-based + no registered user yet: no notification — the banner surfaces naturally
///     on next sign-in via the pending-banner query (#93).</item>
/// </list>
/// Broadcast failures are best-effort and never fail the HTTP response.
/// </summary>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
public class CreateRequestEndpoint(
    IApplicationDbContext db,
    IMongoContext mongo,
    IRealtimeNotifier notifier,
    UserManager<ApplicationUser> userManager,
    TimeProvider timeProvider,
    ILogger<CreateRequestEndpoint> logger)
    : Endpoint<CreateRequestRequest, CreateRequestResponse>
{
    public override void Configure()
    {
        Post("/trainer/photo-diary-requests");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Create a photo diary request";
            s.Description = "Sends a photo diary request to a client via an existing link or a pending invite. " +
                "The coach picks the mode (Once or Every day) and may add a message.";
            s.Responses[StatusCodes.Status200OK] = "Request created";
            s.Responses[StatusCodes.Status400BadRequest] = "Invalid request";
            s.Responses[StatusCodes.Status404NotFound] = "Link, invite or plan not found, or not the caller's";
            s.Responses[StatusCodes.Status409Conflict] = "The caller already has an open request for this client";
        });
    }

    public override async Task HandleAsync(CreateRequestRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null) { await Send.UnauthorizedAsync(ct); return; }
        var professionalId = Guid.Parse(userId);

        // Resolve the professional's profile (needed for ownership checks on link/invite)
        var professionalProfile = await db.ProfessionalProfiles
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.UserId == professionalId, ct);

        if (professionalProfile is null)
        {
            this.ThrowErrorWithCode(ErrorCodes.TrainerProfileMissing, "Professional profile not found.");
            return;
        }

        // ClientProfile.UserId (ApplicationUser.Id) for plan-ownership lookups — Mongo
        // NutritionPlan/TrainingPlan store ClientId as the client's ApplicationUser.Id
        // since #840.
        ClientProfessionalLink? link = null;
        Guid? clientUserId = null;
        string? inviteEmail = null;

        if (req.ClientId.HasValue)
        {
            // Resolve the client's live link to this professional in a single predicate — an
            // unknown clientId, a clientId linked to a DIFFERENT professional, and a clientId
            // whose link is archived (IsActive == false) must all collapse to the same 404, so
            // client existence never leaks to a caller with no active relationship to them.
            link = await db.ClientProfessionalLinks
                .AsNoTracking()
                .Include(l => l.ClientProfile)
                .FirstOrDefaultAsync(l =>
                    l.ClientProfile.PublicId == req.ClientId.Value &&
                    l.ProfessionalProfileId == professionalProfile.Id &&
                    l.ConnectionState != ConnectionState.Ended, ct);

            if (link is null)
            {
                await SendProblemDetailsAsync(404, ErrorCodes.PhotoDiaryRequestLinkNotOwned,
                    "Link not found or does not belong to you.", ct);
                return;
            }

            clientUserId = link.ClientProfile.UserId;
        }
        else if (req.PendingInviteId.HasValue)
        {
            // Verify the invite is owned by this professional
            var invite = await db.PendingInvites
                .AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == req.PendingInviteId.Value, ct);

            if (invite is null || invite.ProfessionalProfileId != professionalProfile.Id)
            {
                await SendProblemDetailsAsync(404, ErrorCodes.PhotoDiaryRequestInviteNotOwned,
                    "Pending invite not found or does not belong to you.", ct);
                return;
            }

            inviteEmail = invite.Email;
            // clientUserId resolved below after save (only if user is already registered)
        }

        // Validate planId if provided (check both nutrition and training plans). The plan must have
        // been created by the caller on either path. On the link-based path (clientUserId is set,
        // so `link` is non-null) it must also belong to that client, and the caller's link must carry
        // the capability flag matching the plan's domain — scoping a diary request to a plan the
        // caller's own link denies is the same cross-domain association the plan-addressed routes gate on.
        if (req.PlanId.HasValue && !await IsPlanUsableAsync(req.PlanId.Value, professionalId, clientUserId, link, ct))
        {
            await SendProblemDetailsAsync(404, ErrorCodes.PhotoDiaryRequestPlanNotOwned,
                "The specified plan was not found or does not belong to this client.", ct);
            return;
        }

        var now = timeProvider.GetUtcNow();

        // Serialise on the coach's profile row so two concurrent creates cannot both pass the
        // open-request check below.
        await using var transaction = await db.BeginTransactionAsync(ct);
        await db.LockProfessionalProfileAsync(professionalProfile.Id, ct);

        // One open request per coach and link (or pending invite). The end date is computed in
        // memory over the coach's few candidate rows rather than inside the query.
        var openCandidateQuery = db.PhotoDiaryRequests
            .AsNoTracking()
            .Where(r => r.ProfessionalId == professionalId
                && (r.Status == PhotoDiaryStatus.Pending
                    || r.Status == PhotoDiaryStatus.Accepted
                    || r.Status == PhotoDiaryStatus.InProgress));

        if (link is not null)
        {
            var linkId = link.Id;
            openCandidateQuery = openCandidateQuery.Where(r => r.LinkId == linkId);
        }
        else
        {
            var inviteId = req.PendingInviteId;
            openCandidateQuery = openCandidateQuery.Where(r => r.PendingInviteId == inviteId);
        }

        var openCandidates = await openCandidateQuery.ToListAsync(ct);

        if (openCandidates.Any(r => r.IsOpenAt(now)))
        {
            await SendProblemDetailsAsync(409, ErrorCodes.PhotoDiaryRequestAlreadyOpen,
                "You already have an open photo request for this client.", ct);
            return;
        }

        var request = new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = professionalId,
            LinkId = link?.Id,
            PendingInviteId = req.PendingInviteId,
            PlanId = req.PlanId,
            Mode = req.Mode,
            DurationDays = req.Mode == PhotoDiaryMode.Bulk ? 1 : req.DurationDays,
            Message = string.IsNullOrWhiteSpace(req.Message) ? null : req.Message,
            Status = PhotoDiaryStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.PhotoDiaryRequests.Add(request);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // ── Emit photoDiaryRequested to the client group (best-effort) ───────────
        // Link-based: client is already known.
        // Invite-based: resolve the registered user by e-mail; skip if not yet registered.
        if (clientUserId is null && inviteEmail is not null)
        {
            var invitedUser = await userManager.FindByEmailAsync(inviteEmail);

            // An invite addressed to a coaching account is a silent row: the coach account must
            // not receive the realtime event, so treat it like an unregistered email.
            var invitedUserIsCoach = invitedUser is not null &&
                await db.ProfessionalProfiles.AsNoTracking().AnyAsync(pp => pp.UserId == invitedUser.Id, ct);

            clientUserId = invitedUserIsCoach ? null : invitedUser?.Id;
        }

        if (clientUserId.HasValue)
        {
            try
            {
                var professionalName =
                    $"{professionalProfile.User.FirstName} {professionalProfile.User.LastName}".Trim();

                // Resolve the professional's role from the claims (Trainer or Nutritionist).
                // FastEndpoints encodes roles under the short "role" claim type, not ClaimTypes.Role.
                var professionalRole = User.FindFirstValue("role")
                    ?? string.Empty;

                await notifier.NotifyAsync(
                    clientUserId.Value,   // → client group
                    "photodiaryrequested",
                    new PhotoDiaryRequestedEvent
                    {
                        RequestId = request.Id,
                        ProfessionalName = professionalName,
                        ProfessionalRole = professionalRole,
                        DurationDays = request.DurationDays,
                        Mode = request.Mode?.ToString(),
                        Message = request.Message,
                        PlanId = request.PlanId,
                        CreatedAt = request.CreatedAt,
                    },
                    ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to emit photoDiaryRequested for request {RequestId} to client {ClientId}",
                    request.Id, clientUserId.Value);
            }
        }
        else if (inviteEmail is not null)
        {
            // No registered user for the invite e-mail yet — notification skipped intentionally.
            logger.LogDebug(
                "Skipping photoDiaryRequested for request {RequestId}: invite e-mail {Email} has no registered user yet",
                request.Id, inviteEmail);
        }

        // Send 200 (not 201) — the project's NSwag-generated TypeScript client only handles
        // 200 as the success branch; a 201 response would be treated as an error and surface
        // in an error toast on the web. The rest of the codebase's POST endpoints follow
        // this same pragmatic convention.
        await Send.OkAsync(new CreateRequestResponse
        {
            Id = request.Id,
            ProfessionalId = request.ProfessionalId,
            LinkId = request.LinkId,
            PendingInviteId = request.PendingInviteId,
            PlanId = request.PlanId,
            DurationDays = request.DurationDays,
            Mode = request.Mode,
            Message = request.Message,
            Status = request.Status,
            CreatedAt = request.CreatedAt,
        }, cancellation: ct);
    }

    /// <summary>
    /// True when the plan was created by the caller and, on the link path, belongs to the link's
    /// client and the matching capability flag is granted.
    /// </summary>
    private async Task<bool> IsPlanUsableAsync(
        Guid planId,
        Guid professionalId,
        Guid? clientUserId,
        ClientProfessionalLink? link,
        CancellationToken ct)
    {
        var nutritionPlan = await (await mongo.NutritionPlans
            .FindAsync(Builders<Domain.Documents.NutritionPlan>.Filter.Eq(p => p.ExternalId, planId),
                cancellationToken: ct))
            .FirstOrDefaultAsync(ct);

        if (nutritionPlan is not null)
        {
            return nutritionPlan.NutritionistId == professionalId
                && (clientUserId is null
                    || (nutritionPlan.ClientId == clientUserId.Value && link is { CanViewNutritionPlans: true }));
        }

        var trainingPlan = await (await mongo.TrainingPlans
            .FindAsync(Builders<Domain.Documents.TrainingPlan>.Filter.Eq(p => p.ExternalId, planId),
                cancellationToken: ct))
            .FirstOrDefaultAsync(ct);

        return trainingPlan is not null
            && trainingPlan.TrainerId == professionalId
            && (clientUserId is null
                || (trainingPlan.ClientId == clientUserId.Value && link is { CanViewTrainingPlans: true }));
    }

    private async Task SendProblemDetailsAsync(int statusCode, string errorCode, string detail, CancellationToken ct)
    {
        await this.SendProblemAsync(statusCode, errorCode, detail, ct);
    }
}
