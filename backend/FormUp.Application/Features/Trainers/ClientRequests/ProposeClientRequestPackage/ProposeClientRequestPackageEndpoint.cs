using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Trainers.ClientRequests.ProposeClientRequestPackage;

/// <summary>
/// A professional proposes one of their packages on a pending client request that has no package, and the
/// client then accepts or declines it.
/// </summary>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
public class ProposeClientRequestPackageEndpoint(
    IApplicationDbContext db,
    IRealtimeNotifier notifier,
    INotificationService notificationService,
    IConversationSeedService conversationSeedService,
    EntitlementService entitlementService,
    TimeProvider timeProvider,
    ILogger<ProposeClientRequestPackageEndpoint> logger)
    : Endpoint<ProposeClientRequestPackageRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/trainer/client-requests/{PublicId}/propose-package");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist, AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "Propose a package on a client request";
            s.Description = "Moves a pending request that carries no package to PackageProposed, with an optional discount, " +
                "form and message, and tells the client. The client then accepts or declines the proposal.";
            s.Responses[StatusCodes.Status204NoContent] = "Proposal sent";
            s.Responses[StatusCodes.Status400BadRequest] =
                "Invalid request, a Check-in form (CHECKIN_FORM_NOT_SENDABLE), or a fixed discount in another currency than the package (CURRENCY_MISMATCH)";
            s.Responses[StatusCodes.Status403Forbidden] =
                "The coach role was removed, or the subscription does not include payments (PAYMENTS_NOT_ENTITLED)";
            s.Responses[StatusCodes.Status404NotFound] =
                "The request is unknown or not the caller's (CLIENT_REQUEST_NOT_FOUND), or the package or discount is unknown or another coach's";
            s.Responses[StatusCodes.Status409Conflict] =
                "The request is not pending or already carries a package (CLIENT_REQUEST_INVALID_STATE), or the package is archived (PACKAGE_ARCHIVED), " +
                "the discount is archived (DISCOUNT_ARCHIVED) or does not apply to the package (DISCOUNT_PACKAGE_INVALID)";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(ProposeClientRequestPackageRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var userGuid = Guid.Parse(userId);

        var professionalProfile = await db.ProfessionalProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(pp => pp.UserId == userGuid, ct);

        if (professionalProfile is null)
        {
            this.ThrowErrorWithCode(ErrorCodes.TrainerProfileMissing, "Professional profile not found.");
            return;
        }

        var clientRequest = await db.ClientRequests
            .Include(r => r.ClientProfile)
            .FirstOrDefaultAsync(r => r.PublicId == req.PublicId && r.ProfessionalProfileId == professionalProfile.Id, ct);

        if (clientRequest is null)
        {
            await this.SendProblemAsync(StatusCodes.Status404NotFound, ErrorCodes.ClientRequestNotFound, "Client request not found.", ct);
            return;
        }

        // Only a pending request without a client-chosen package takes a proposal.
        if (clientRequest.Status != ClientRequestStatus.Pending || clientRequest.CoachPackageId is not null)
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict,
                ErrorCodes.ClientRequestInvalidState,
                "A package can only be proposed on a pending request that has no package.",
                ct);
            return;
        }

        var (package, discount) = await LoadPackageTermsOrRespondAsync(req, professionalProfile.Id, ct);

        if (package is null)
        {
            return;
        }

        // An unknown or foreign questionnaire id is ignored, as when accepting; a Check-in form is refused.
        Questionnaire? questionnaire = null;

        if (req.QuestionnairePublicId.HasValue)
        {
            questionnaire = await db.Questionnaires
                .FirstOrDefaultAsync(q => q.PublicId == req.QuestionnairePublicId.Value && q.ProfessionalId == userGuid, ct);

            if (questionnaire?.Type == QuestionnaireType.CheckIn)
            {
                this.ThrowErrorWithCode(ErrorCodes.CheckInFormNotSendable, "Check-in forms cannot be sent as a questionnaire.");
                return;
            }
        }

        clientRequest.Status = ClientRequestStatus.PackageProposed;
        clientRequest.ProposedPackageId = package.Id;
        clientRequest.ProposedDiscountId = discount?.Id;
        clientRequest.ProposedAt = timeProvider.GetUtcNow().UtcDateTime;
        clientRequest.QuestionnaireId = questionnaire?.Id;
        clientRequest.SkipOnboardingForm = req.SkipOnboardingForm;
        clientRequest.Statement = req.Statement;

        await db.SaveChangesAsync(ct);

        var profUser = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userGuid, ct);
        var profName = $"{profUser.FirstName} {profUser.LastName}";

        await notificationService.CreateAsync(
            clientRequest.ClientProfile.UserId,
            NotificationType.ClientRequestPackageProposed,
            new Dictionary<string, string>
            {
                ["profName"] = profName,
                ["requestId"] = clientRequest.PublicId.ToString(),
            },
            ct: ct);

        await notifier.NotifyAsync(clientRequest.ClientProfile.UserId, "clientrequestpackageproposed", new
        {
            RequestPublicId = clientRequest.PublicId,
            ProfessionalPublicId = professionalProfile.PublicId,
            ProfessionalName = profName,
        }, ct);

        // The coach's ProposalSent event, only into a thread that already exists. The statement is not a chat message.
        await conversationSeedService.AppendCooperationEventAsync(
            userGuid,
            clientRequest.ClientProfile.UserId,
            userGuid,
            ChatEventType.ProposalSent,
            clientRequest.PublicId,
            messageText: null,
            createConversationIfMissing: false,
            ct);

        logger.LogInformation(
            "Package proposed on client request {RequestId} by professional {ProfessionalId}",
            clientRequest.PublicId, professionalProfile.PublicId);

        await Send.NoContentAsync(ct);
    }

    /// <summary>
    /// Loads the package and discount and checks them against <see cref="PackageTermsRules"/>. Answers the failure
    /// and returns a null package when they cannot be proposed.
    /// </summary>
    private async Task<(CoachPackage? Package, CoachDiscount? Discount)> LoadPackageTermsOrRespondAsync(
        ProposeClientRequestPackageRequest req, long professionalProfileId, CancellationToken ct)
    {
        var entitlements = await entitlementService.GetEntitlementsAsync(professionalProfileId, ct);

        var package = await db.CoachPackages
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.PublicId == req.PackagePublicId && p.ProfessionalProfileId == professionalProfileId, ct);

        var discount = req.DiscountPublicId is not { } discountPublicId
            ? null
            : await db.CoachDiscounts
                .AsNoTracking()
                .Include(d => d.Packages)
                .FirstOrDefaultAsync(d => d.PublicId == discountPublicId && d.ProfessionalProfileId == professionalProfileId, ct);

        var failure = PackageTermsRules.Check(entitlements.CanUsePackages && entitlements.CanUsePayments, package, req.DiscountPublicId is not null, discount);

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
}
