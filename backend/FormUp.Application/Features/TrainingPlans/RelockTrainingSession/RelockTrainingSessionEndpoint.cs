using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;

namespace FormUp.Application.Features.TrainingPlans.RelockTrainingSession;

/// <summary>
/// Releases the Editing lock on a training session, returning it to Stable state.
/// Idempotent: if the lock is already gone (released, expired) this is a no-op success.
/// Ownership guard: only the plan's owning trainer may relock.
/// Emits <c>sessioneditlockchanged</c> (state=Stable) to both client and trainer when a lock is actually released.
/// Only emits on a real state transition (ReleaseAsync returns true).
/// </summary>
[RemovedCoachRole(AppRoles.Trainer, RemovedCoachRoleMode.WhilePlanInProgress)]
public class RelockTrainingSessionEndpoint(
    IApplicationDbContext db,
    IMongoContext mongo,
    ISessionLockService lockService,
    IRealtimeNotifier notifier,
    IClientLinkAuthorizationService linkAuthorizationService)
    : Endpoint<RelockTrainingSessionRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/training/plans/{PlanId}/sessions/{SessionId}/relock");
        Roles(AppRoles.Trainer);
        Summary(s =>
        {
            s.Summary = "Relock a training session (release the Editing lock)";
            s.Description = "Releases the Editing lock, returning the session to Stable state. " +
                            "Idempotent: if the lock is already released or expired this is a no-op success.";
            s.Responses[StatusCodes.Status403Forbidden] =
                "The client is over the Free plan's client limit and the plan's last published week has ended (CLIENT_OVER_PLAN_LIMIT)";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(RelockTrainingSessionRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var trainerId = Guid.Parse(userId);

        // Authorship + link guard first: the plan must exist, be the calling trainer's, and the
        // caller's link to its client must still grant training access.
        var plan = await this.LoadOwnedTrainingPlanIfAllowedAsync(mongo, linkAuthorizationService, req.PlanId, trainerId, ct);

        if (plan is null)
        {
            return;
        }

        // A client over the Free plan's cap stays editable only until the last published week ends.
        var overCapAccess = await this.RespondIfClientOverPlanLimitAfterRunsUntilAsync(
            db,
            trainerId,
            plan.ClientId,
            PlanRunsUntilResolver.Resolve(
                plan.StartDate, plan.Weeks.Where(w => w.Status == WeekStatus.Published).Select(w => w.WeekNumber)),
            ct);

        if (overCapAccess == OverCapEditAccess.Refused)
        {
            return;
        }

        // Verify the session exists in the plan (any week).
        var sessionExists = plan.Weeks
            .SelectMany(w => w.Days)
            .SelectMany(d => d.Sessions)
            .Any(s => s.SessionId == req.SessionId);

        if (!sessionExists)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Release idempotently: ReleaseAsync returns false when no doc matched
        // (already released/expired), but that is still a success per the spec.
        // Only emit sessioneditlockchanged when ReleaseAsync returns true — emitting Stable
        // for a session that had no lock would be spurious fan-out.
        var released = await lockService.ReleaseAsync(
            sessionId: req.SessionId,
            holder: LockHolder.Coach,
            type: LockType.Editing,
            ct: ct);

        if (released)
        {
            var payload = new SessionLockChangedPayload(
                plan.ExternalId,
                req.SessionId,
                "Stable",
                "Coach");

            await notifier.NotifyAsync(plan.ClientId, "sessioneditlockchanged", payload, ct);
            await notifier.NotifyAsync(trainerId, "sessioneditlockchanged", payload, ct);
        }

        await Send.NoContentAsync(ct);
    }
}
