using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.NutritionPlans.GetPlan;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FormUp.Application.Features.NutritionPlans.PublishWeek;

/// <summary>
/// Publishes a single week of a nutrition plan, making it visible to the client.
/// When the first week is published, supersedes (archives) other Active plans for the same
/// client ONLY if their date window overlaps this plan's window — non-overlapping Active plans
/// (e.g. a past or future plan) are left untouched (#780).
/// </summary>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.WhilePlanInProgress)]
public class PublishWeekEndpoint(
    IMongoContext mongo,
    IApplicationDbContext db,
    INotificationService notificationService,
    IRealtimeNotifier notifier,
    PlanConcurrencyGuard guard,
    IClientLinkAuthorizationService linkAuthorizationService) : Endpoint<PublishWeekRequest, GetPlanResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/nutrition/plans/{PlanId}/weeks/{WeekNumber}/publish");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Publish a week of a nutrition plan";
            s.Description = "Sets the week's status to Published. Archives other Active plans for the same client whose date window overlaps this plan's.";
            s.Responses[StatusCodes.Status403Forbidden] =
                "The subscription does not include plans (SUBSCRIPTION_REQUIRED), or the client is over the Free plan's client limit (CLIENT_OVER_PLAN_LIMIT)";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(PublishWeekRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var nutritionistId = Guid.Parse(userId);

        var lookupFilter = Builders<NutritionPlan>.Filter.Eq(p => p.ExternalId, req.PlanId)
                     & Builders<NutritionPlan>.Filter.Eq(p => p.NutritionistId, nutritionistId);

        // Computed inside the validate delegate BEFORE the write (reflects pre-publish state);
        // consumed after a confirmed successful update to decide whether to archive siblings.
        var hadPublishedWeeks = false;

        // The lookup filter proved authorship, which is permanent. Access is not — require the
        // caller's link to the plan's client to still grant nutrition access.
        async Task<bool> Authorize(NutritionPlan plan, CancellationToken authorizeCt)
        {
            // plan.ClientId is ApplicationUser.Id (#840) — the UserId-addressed overload.
            var capabilities = await linkAuthorizationService.GetCapabilitiesByClientUserIdAsync(
                nutritionistId, plan.ClientId, authorizeCt);

            if (capabilities is not { CanViewNutritionPlans: true })
            {
                await Send.NotFoundAsync(authorizeCt);
                return false;
            }

            // The subscription gate comes after the lookup, so a missing or foreign plan still answers 404.
            if (await this.RespondIfFeatureNotEntitledAsync(
                    db, nutritionistId, entitlements => entitlements.CanCreatePlans,
                    "The current subscription does not include publishing plans.", authorizeCt))
            {
                return false;
            }

            // A client over the Free plan's cap after a lapse gets no new published weeks.
            return !await this.RespondIfClientOverPlanLimitAsync(db, nutritionistId, plan.ClientId, authorizeCt);
        }

        Task<bool> Validate(NutritionPlan plan, CancellationToken validateCt)
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
        var writeFilter = Builders<NutritionPlan>.Filter.Eq(p => p.ExternalId, req.PlanId)
            & Builders<NutritionPlan>.Filter.Eq(p => p.NutritionistId, nutritionistId)
            & Builders<NutritionPlan>.Filter.Ne(p => p.Status, NutritionPlanStatus.Archived)
            & Builders<NutritionPlan>.Filter.ElemMatch(p => p.Weeks,
                w => w.WeekNumber == req.WeekNumber && w.Status != WeekStatus.Published);

        var update = Builders<NutritionPlan>.Update
            .Set("weeks.$[w].status", WeekStatus.Published.ToString())
            .Set("weeks.$[w].datePublished", now)
            .Set(p => p.Status, NutritionPlanStatus.Active)
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
            mongo.NutritionPlans,
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

        // Now that the publish itself is confirmed, supersede other Active plans of the same
        // type for this client — but ONLY those whose date window overlaps this plan's window.
        // A client may legitimately hold several sequential, non-overlapping Active plans at
        // once (#780); publishing a March plan must not archive a still-relevant January plan.
        // Plans without a StartDate are unranged and are neither archived nor allowed to block —
        // in practice this plan already required a StartDate to publish (see StartDateRequired
        // above), so only the OTHER side of the comparison needs the null-guard.
        if (!hadPublishedWeeks)
        {
            // Only the caller's OWN plans are superseded. Without the author predicate this
            // archives every overlapping Active plan for the client regardless of who wrote it,
            // which lets one professional destroy another's live plan.
            var siblingFilter = Builders<NutritionPlan>.Filter.Eq(p => p.ClientId, plan.ClientId)
                                & Builders<NutritionPlan>.Filter.Eq(p => p.NutritionistId, nutritionistId)
                                & Builders<NutritionPlan>.Filter.Eq(p => p.Status, NutritionPlanStatus.Active)
                                & Builders<NutritionPlan>.Filter.Ne(p => p.ExternalId, plan.ExternalId);

            using var siblingCursor = await mongo.NutritionPlans.FindAsync(siblingFilter, cancellationToken: ct);
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
                var archiveFilter = Builders<NutritionPlan>.Filter.In(p => p.ExternalId, overlappingIds)
                                    & Builders<NutritionPlan>.Filter.Eq(p => p.NutritionistId, nutritionistId);

                // The Version bump keeps a concurrent version-gated replace from writing the
                // pre-archival document back and resurrecting the superseded plan as Active; it
                // becomes a 409 instead.
                var archiveUpdate = Builders<NutritionPlan>.Update
                    .Set(p => p.Status, NutritionPlanStatus.Archived)
                    .Set(p => p.DateUpdated, DateTime.UtcNow)
                    .Inc(p => p.Version, 1);

                await mongo.NutritionPlans.UpdateManyAsync(archiveFilter, archiveUpdate, cancellationToken: ct);
            }
        }

        // Notify the client about the published week — NutritionPlan.ClientId is
        // ApplicationUser.Id (#840).
        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.UserId == plan.ClientId, ct);

        if (clientProfile is not null)
        {
            await notificationService.CreateAsync(
                clientProfile.UserId,
                NotificationType.PlanPublished,
                new Dictionary<string, string>
                {
                    ["weekNumber"] = req.WeekNumber.ToString(),
                    [NotificationTemplates.PlanKindKey] = NotificationTemplates.PlanKindNutrition,
                    [NotificationTemplates.PlanIdKey] = plan.ExternalId.ToString(),
                },
                variant: NotificationTemplates.PlanPublishedNutritionPublished,
                ct: ct);

            await notifier.NotifyAsync(clientProfile.UserId, "nutritionplanpublished", new
            {
                PlanId = plan.ExternalId,
                req.WeekNumber,
            }, ct);
        }

        // Response ClientId must stay the client-facing ClientProfile.PublicId (pre-#840
        // contract) — reuse the profile already resolved above instead of a second lookup.
        var clientPublicId = clientProfile?.PublicId ?? plan.ClientId;
        await Send.OkAsync(GetPlanResponse.FromDocument(plan, clientPublicId), ct);
    }
}
