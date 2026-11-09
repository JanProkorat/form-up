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
using FormUp.Application.Infrastructure.Services;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace FormUp.Application.Features.TrainingPlans.UnlockTrainingSession;

/// <summary>
/// Acquires an Editing lock on a published training session, allowing the trainer to edit it.
/// Returns 409 if the session is currently in Live state (client is training).
/// The ownership guard ensures only the plan's owning trainer may unlock.
/// Emits <c>sessioneditlockchanged</c> (state=Editing) to both client and trainer on successful acquire.
/// </summary>
[RemovedCoachRole(AppRoles.Trainer, RemovedCoachRoleMode.WhilePlanInProgress)]
public class UnlockTrainingSessionEndpoint(
    IApplicationDbContext db,
    IMongoContext mongo,
    ISessionLockService lockService,
    IOptions<TrainingLockOptions> lockOptions,
    IRealtimeNotifier notifier,
    IClientLinkAuthorizationService linkAuthorizationService)
    : Endpoint<UnlockTrainingSessionRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/training/plans/{PlanId}/sessions/{SessionId}/unlock");
        Roles(AppRoles.Trainer);
        Summary(s =>
        {
            s.Summary = "Unlock a training session for editing";
            s.Description = "Acquires an Editing lock on the session. " +
                            "Returns 409 if the session is currently Live (client is training).";
            s.Responses[StatusCodes.Status403Forbidden] =
                "The client is over the Free plan's client limit and the plan's last published week has ended (CLIENT_OVER_PLAN_LIMIT)";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(UnlockTrainingSessionRequest req, CancellationToken ct)
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
        var session = plan.Weeks
            .SelectMany(w => w.Days)
            .SelectMany(d => d.Sessions)
            .FirstOrDefault(s => s.SessionId == req.SessionId);

        if (session is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Finished-guard: reject unlock attempts on sessions that are already finished.
        // #841: both signals (finished live workout, home-checkbox completion) now live on the
        // SAME SessionExecution document — a single query + IsSessionComplete() covers both.
        // Match any execution for this session regardless of date — finished state is permanent.
        // Checked AFTER ownership/existence guards (404 takes precedence) and BEFORE acquiring the Editing lock.
        var executionFilter = Builders<SessionExecution>.Filter.Eq(c => c.ClientId, plan.ClientId)
                               & Builders<SessionExecution>.Filter.Eq(c => c.SessionId, req.SessionId);
        var executionCursor = await mongo.SessionExecutions.FindAsync(executionFilter, cancellationToken: ct);
        var executionDocs = await executionCursor.ToListAsync(ct);

        if (executionDocs.Any(e => e.IsSessionComplete(session)))
        {
            await this.SendProblemAsync(409, ErrorCodes.SessionAlreadyCompleted,
                "This session has already been completed and cannot be unlocked for editing.", ct);
            return;
        }

        var ttl = TimeSpan.FromHours(lockOptions.Value.EditingTtlHours);

        var result = await lockService.AcquireAsync(
            sessionId: req.SessionId,
            planId: plan.ExternalId,
            clientId: plan.ClientId,
            trainerId: trainerId,
            holder: LockHolder.Coach,
            type: LockType.Editing,
            ttl: ttl,
            ct: ct);

        switch (result)
        {
            case AcquireResult.Acquired:
                // Emit sessioneditlockchanged (state=Editing) to both parties on successful acquire.
                var payload = new SessionLockChangedPayload(
                    plan.ExternalId,
                    req.SessionId,
                    "Editing",
                    "Coach");

                await notifier.NotifyAsync(plan.ClientId, "sessioneditlockchanged", payload, ct);
                await notifier.NotifyAsync(trainerId, "sessioneditlockchanged", payload, ct);

                await Send.NoContentAsync(ct);
                break;

            case AcquireResult.LockConflict:
                // 409 conflict path — emit nothing; no state transition occurred.
                // The session is currently locked — either Live (client training) or
                // already in Editing by someone else. Both cases are 409 session_locked.
                await this.SendProblemAsync(
                    409,
                    ErrorCodes.SessionLocked,
                    $"Session {req.SessionId} is currently locked and cannot be unlocked for editing.",
                    ct);
                break;
        }
    }
}
