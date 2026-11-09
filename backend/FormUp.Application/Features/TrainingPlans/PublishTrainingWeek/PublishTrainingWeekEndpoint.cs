using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.TrainingPlans.GetTrainingPlan;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FormUp.Application.Features.TrainingPlans.PublishTrainingWeek;

/// <summary>
/// Publishes a single week of a training plan, making it visible to the client.
/// When the first week is published, supersedes (archives) other Active training plans for the
/// same client ONLY if their date window overlaps this plan's window — non-overlapping Active
/// plans (e.g. a past or future plan) are left untouched (#780).
/// Defensively clears any stale Editing lock docs for the week's sessions on publish.
/// </summary>
[RemovedCoachRole(AppRoles.Trainer, RemovedCoachRoleMode.WhilePlanInProgress)]
public class PublishTrainingWeekEndpoint(
    IMongoContext mongo,
    IApplicationDbContext db,
    INotificationService notificationService,
    IRealtimeNotifier notifier,
    ISessionLockService lockService,
    PlanConcurrencyGuard guard,
    IClientLinkAuthorizationService linkAuthorizationService) : Endpoint<PublishTrainingWeekRequest, GetTrainingPlanResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/training/plans/{PlanId}/weeks/{WeekNumber}/publish");
        Roles(AppRoles.Trainer);
        Summary(s =>
        {
            s.Summary = "Publish a week of a training plan";
            s.Description = "Sets the week's status to Published. Archives other Active training plans for the same client whose date window overlaps this plan's. " +
                            "Clears any stale Editing lock docs for the week's sessions.";
            s.Responses[StatusCodes.Status403Forbidden] =
                "The subscription does not include plans (SUBSCRIPTION_REQUIRED), or the client is over the Free plan's client limit (CLIENT_OVER_PLAN_LIMIT)";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(PublishTrainingWeekRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var trainerId = Guid.Parse(userId);

        var lookupFilter = Builders<TrainingPlan>.Filter.Eq(p => p.ExternalId, req.PlanId)
                     & Builders<TrainingPlan>.Filter.Eq(p => p.TrainerId, trainerId);

        // Computed inside the validate delegate BEFORE the write (reflects pre-publish state);
        // consumed after a confirmed successful update to decide whether to archive siblings.
        var hadPublishedWeeks = false;

        // The lookup filter proved authorship, which is permanent. Access is not — require the
        // caller's link to the plan's client to still grant training access.
        async Task<bool> Authorize(TrainingPlan plan, CancellationToken authorizeCt)
        {
            // plan.ClientId is ApplicationUser.Id (#840) — the UserId-addressed overload.
            var capabilities = await linkAuthorizationService.GetCapabilitiesByClientUserIdAsync(
                trainerId, plan.ClientId, authorizeCt);

            if (capabilities is not { CanViewTrainingPlans: true })
            {
                await Send.NotFoundAsync(authorizeCt);
                return false;
            }

            // The subscription gate comes after the lookup, so a missing or foreign plan still answers 404.
            if (await this.RespondIfFeatureNotEntitledAsync(
                    db, trainerId, entitlements => entitlements.CanCreatePlans,
                    "The current subscription does not include publishing plans.", authorizeCt))
            {
                return false;
            }

            // A client over the Free plan's cap after a lapse gets no new published weeks.
            return !await this.RespondIfClientOverPlanLimitAsync(db, trainerId, plan.ClientId, authorizeCt);
        }

        Task<bool> Validate(TrainingPlan plan, CancellationToken validateCt)
        {
            var week = plan.Weeks.FirstOrDefault(w => w.WeekNumber == req.WeekNumber);
            if (week is null)
            {
                ThrowError($"Week {req.WeekNumber} not found in plan.");
                return Task.FromResult(false);
            }

            if (week.Status == WeekStatus.Published)
            {
                ThrowError($"Week {req.WeekNumber} is already published.");
                return Task.FromResult(false);
            }

            // Start date must be set before publishing
            if (!plan.StartDate.HasValue)
            {
                ThrowError(ErrorCodes.StartDateRequired, "Start date must be set before publishing a week.");
                return Task.FromResult(false);
            }

            // The target week's Monday must not be in the past
            var weekStartDate = DateOnly.FromDateTime(plan.StartDate.Value.AddDays((req.WeekNumber - 1) * 7));
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (weekStartDate < today)
            {
                ThrowError(ErrorCodes.WeekStartInPast, $"Week {req.WeekNumber} starts on {weekStartDate}, which is in the past.");
                return Task.FromResult(false);
            }

            // Check if this is the first published week — if so, archive other active plans
            // afterward. Computed BEFORE the write below so it reflects pre-publish state.
            hadPublishedWeeks = plan.Weeks.Any(w => w.Status == WeekStatus.Published);

            return Task.FromResult(true);
        }

        var now = DateTime.UtcNow;

        // Targeted $set on the matched week only (#839 — replaces the previous full-document
        // ReplaceOneAsync). The write filter's ElemMatch gates on the TARGET WEEK still being
        // unpublished — NOT on the document's Version — so a concurrent edit to an unrelated
        // week/field never produces a false 409 (AC#4), while a genuine race that publishes the
        // SAME week between our fetch and this write causes zero documents to match (AC#2/#7).
        // The Ne(Archived) predicate is a security guard, not a state check. This update sets
        // Status = Active unconditionally, and this path has no version comparison, so the
        // Version bump on an archival is invisible to it. Without this predicate a publish that
        // passed its link check microseconds before the plan was archived — by an ending
        // collaboration, or by a sibling plan superseding this one — would still match here and
        // set the plan back to Active, resurrecting a plan whose author no longer has a live
        // link and which the client would then be served.
        var writeFilter = Builders<TrainingPlan>.Filter.Eq(p => p.ExternalId, req.PlanId)
            & Builders<TrainingPlan>.Filter.Eq(p => p.TrainerId, trainerId)
            & Builders<TrainingPlan>.Filter.Ne(p => p.Status, TrainingPlanStatus.Archived)
            & Builders<TrainingPlan>.Filter.ElemMatch(p => p.Weeks,
                w => w.WeekNumber == req.WeekNumber && w.Status != WeekStatus.Published);

        var update = Builders<TrainingPlan>.Update
            .Set("weeks.$[w].status", WeekStatus.Published.ToString())
            .Set("weeks.$[w].datePublished", now)
            .Set(p => p.Status, TrainingPlanStatus.Active)
            .Set(p => p.DateUpdated, now)
            .Inc(p => p.Version, 1);

        var arrayFilters = new List<ArrayFilterDefinition>
        {
            new BsonDocumentArrayFilterDefinition<BsonDocument>(new BsonDocument
            {
                { "w.weekNumber", req.WeekNumber },
                { "w.status", new BsonDocument("$ne", WeekStatus.Published.ToString()) }
            })
        };

        var guardResult = await guard.UpdateWithArrayFilterGuardAsync(
            mongo.TrainingPlans,
            lookupFilter,
            Authorize,
            Validate,
            writeFilter,
            update,
            arrayFilters,
            ct);

        switch (guardResult.Outcome)
        {
            case PlanConcurrencyOutcome.NotFound:
                await Send.NotFoundAsync(ct);
                return;
            case PlanConcurrencyOutcome.ReplaceConflict:
                await this.SendProblemAsync(409, ErrorCodes.PlanVersionConflict,
                    "Version conflict. The week was modified concurrently.", ct);
                return;
            case PlanConcurrencyOutcome.HandledByMutator:
                // The authorize delegate already wrote its 404.
                return;
        }

        var plan = guardResult.Document!;

        // Now that the publish itself is confirmed, supersede other Active training plans of the
        // same client — but ONLY those whose date window overlaps this plan's window. A client
        // may legitimately hold several sequential, non-overlapping Active plans at once (#780);
        // publishing a March plan must not archive a still-relevant January plan. Plans without a
        // StartDate are unranged and are neither archived nor allowed to block — this plan itself
        // already required a StartDate to publish (see StartDateRequired above), so only the
        // OTHER side of the comparison needs the null-guard.
        if (!hadPublishedWeeks)
        {
            // Only the caller's OWN plans are superseded. Without the author predicate this
            // archives every overlapping Active plan for the client regardless of who wrote it,
            // which lets one professional destroy another's live plan.
            var siblingFilter = Builders<TrainingPlan>.Filter.Eq(p => p.ClientId, plan.ClientId)
                                & Builders<TrainingPlan>.Filter.Eq(p => p.TrainerId, trainerId)
                                & Builders<TrainingPlan>.Filter.Eq(p => p.Status, TrainingPlanStatus.Active)
                                & Builders<TrainingPlan>.Filter.Ne(p => p.ExternalId, plan.ExternalId);

            using var siblingCursor = await mongo.TrainingPlans.FindAsync(siblingFilter, cancellationToken: ct);
            var siblings = await siblingCursor.ToListAsync(ct);

            var overlappingIds = siblings
                .Where(s => s.ExternalId != plan.ExternalId) // defensive — the sibling filter already excludes self
                .Where(s => s.StartDate.HasValue
                            && PlanWindowResolver.WindowsOverlap(
                                plan.StartDate!.Value, plan.Weeks.Count,
                                s.StartDate.Value, s.Weeks.Count))
                .Select(s => s.ExternalId)
                .ToList();

            if (overlappingIds.Count > 0)
            {
                var archiveFilter = Builders<TrainingPlan>.Filter.In(p => p.ExternalId, overlappingIds)
                                    & Builders<TrainingPlan>.Filter.Eq(p => p.TrainerId, trainerId);

                // The Version bump keeps a concurrent version-gated replace from writing the
                // pre-archival document back and resurrecting the superseded plan as Active; it
                // becomes a 409 instead.
                var archiveUpdate = Builders<TrainingPlan>.Update
                    .Set(p => p.Status, TrainingPlanStatus.Archived)
                    .Set(p => p.DateUpdated, DateTime.UtcNow)
                    .Inc(p => p.Version, 1);

                await mongo.TrainingPlans.UpdateManyAsync(archiveFilter, archiveUpdate, cancellationToken: ct);
            }
        }

        // Defensive cleanup: clear any stale Editing lock docs for the week's sessions.
        // This handles the edge case where a trainer had a session unlocked just before publish.
        // ReleaseAsync is idempotent — safe to call even if no lock exists.
        // Only emit sessioneditlockchanged when ReleaseAsync returns true — emitting Stable
        // for a session that had no lock would be spurious fan-out.
        var week = plan.Weeks.First(w => w.WeekNumber == req.WeekNumber);
        var weekSessionIds = week.Days.SelectMany(d => d.Sessions).Select(s => s.SessionId).ToList();
        foreach (var sessionId in weekSessionIds)
        {
            var released = await lockService.ReleaseAsync(sessionId, LockHolder.Coach, LockType.Editing, ct);

            if (released)
            {
                var lockPayload = new SessionLockChangedPayload(
                    plan.ExternalId,
                    sessionId,
                    "Stable",
                    "Coach");

                await notifier.NotifyAsync(plan.ClientId, "sessioneditlockchanged", lockPayload, ct);
                await notifier.NotifyAsync(trainerId, "sessioneditlockchanged", lockPayload, ct);
            }
        }

        // Notify the client about the published week — TrainingPlan.ClientId is
        // ApplicationUser.Id (#840).
        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.UserId == plan.ClientId, ct);

        if (clientProfile is not null)
        {
            await notificationService.CreateAsync(
                clientProfile.UserId,
                NotificationType.PlanPublished,
                new Dictionary<string, string> { ["weekNumber"] = req.WeekNumber.ToString() },
                variant: NotificationTemplates.PlanPublishedTrainingPublished,
                ct: ct);

            await notifier.NotifyAsync(clientProfile.UserId, "trainingplanpublished", new
            {
                PlanId = plan.ExternalId,
                PlanName = plan.Name,
                req.WeekNumber,
                StartDate = plan.StartDate,
            }, ct);
        }

        // Response ClientId must stay the client-facing ClientProfile.PublicId (pre-#840
        // contract) — reuse the profile already resolved above instead of a second lookup.
        var clientPublicId = clientProfile?.PublicId ?? plan.ClientId;
        await Send.OkAsync(GetTrainingPlanResponse.FromDocument(plan, clientPublicId), ct);
    }
}
