using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.CoachSubscriptions.Shared;

/// <summary>
/// Finds, per client, how long a coach's published plan weeks keep running.
/// </summary>
public static class ClientPlanRunsUntilLoader
{
    /// <summary>
    /// The latest end of published weeks across the coach's own nutrition and training plans, per client
    /// (keyed by <c>ApplicationUser.Id</c>, as Mongo plans store it). Archived plans no longer run and
    /// other coaches' plans are ignored. Clients without a published week are absent.
    /// </summary>
    /// <param name="mongo">MongoDB context.</param>
    /// <param name="coachUserId">The coach's <c>ApplicationUser.Id</c>.</param>
    /// <param name="clientUserIds">The clients' <c>ApplicationUser.Id</c> values.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<Dictionary<Guid, DateOnly>> LoadAsync(
        IMongoContext mongo, Guid coachUserId, IReadOnlyCollection<Guid> clientUserIds, CancellationToken ct)
    {
        var runsUntil = new Dictionary<Guid, DateOnly>();

        if (clientUserIds.Count == 0)
        {
            return runsUntil;
        }

        var nutritionPlans = await mongo.NutritionPlans
            .Find(Builders<NutritionPlan>.Filter.Eq(p => p.NutritionistId, coachUserId)
                  & Builders<NutritionPlan>.Filter.In(p => p.ClientId, clientUserIds)
                  & Builders<NutritionPlan>.Filter.Ne(p => p.Status, NutritionPlanStatus.Archived))
            .Project(p => new { p.ClientId, p.StartDate, p.Weeks })
            .ToListAsync(ct);

        foreach (var plan in nutritionPlans)
        {
            Record(runsUntil, plan.ClientId, PlanRunsUntilResolver.Resolve(
                plan.StartDate, plan.Weeks.Where(w => w.Status == WeekStatus.Published).Select(w => w.WeekNumber)));
        }

        var trainingPlans = await mongo.TrainingPlans
            .Find(Builders<TrainingPlan>.Filter.Eq(p => p.TrainerId, coachUserId)
                  & Builders<TrainingPlan>.Filter.In(p => p.ClientId, clientUserIds)
                  & Builders<TrainingPlan>.Filter.Ne(p => p.Status, TrainingPlanStatus.Archived))
            .Project(p => new { p.ClientId, p.StartDate, p.Weeks })
            .ToListAsync(ct);

        foreach (var plan in trainingPlans)
        {
            Record(runsUntil, plan.ClientId, PlanRunsUntilResolver.Resolve(
                plan.StartDate, plan.Weeks.Where(w => w.Status == WeekStatus.Published).Select(w => w.WeekNumber)));
        }

        return runsUntil;
    }

    /// <summary>
    /// Whether published weeks ending on <paramref name="lastDay"/> still run on <paramref name="today"/>
    /// (the last day itself still counts).
    /// </summary>
    /// <param name="lastDay">The last day the published weeks cover.</param>
    /// <param name="today">The current UTC date.</param>
    public static bool StillRuns(DateOnly lastDay, DateOnly today) => today <= lastDay;

    private static void Record(Dictionary<Guid, DateOnly> runsUntil, Guid clientUserId, DateOnly? planRunsUntil)
    {
        if (planRunsUntil is { } lastDay
            && (!runsUntil.TryGetValue(clientUserId, out var current) || lastDay > current))
        {
            runsUntil[clientUserId] = lastDay;
        }
    }
}
