using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Locates a meal that is no longer in a client's current plan, for recording an offline-sync conflict.
/// </summary>
public static class RemovedMealFinder
{
    /// <summary>
    /// Finds where a meal that is no longer in the current plan went: a plan of the client's own
    /// (any status) that still contains it, or that recorded its removal in
    /// <see cref="NutritionPlan.RemovedMeals"/>. Null when the id is unknown to the client.
    /// </summary>
    public static async Task<(NutritionPlan Plan, MealKind Kind, int? ChangedAtPlanVersion)?> FindRemovedMealAsync(
        IMongoContext mongo, Guid clientId, Guid mealId, CancellationToken ct)
    {
        var containing = await FindOwnPlanContainingMealAsync(mongo, clientId, mealId, ct);

        if (containing is not null)
        {
            var meal = CurrentPlanMealResolver.FindMeal(containing, mealId)!;
            return (containing, meal.Kind, meal.ChangedAtPlanVersion);
        }

        var recordFilter = Builders<NutritionPlan>.Filter.Eq(p => p.ClientId, clientId)
                           & Builders<NutritionPlan>.Filter.ElemMatch(
                               p => p.RemovedMeals, Builders<RemovedPlanMeal>.Filter.Eq(r => r.MealId, mealId));

        using var cursor = await mongo.NutritionPlans.FindAsync(recordFilter, cancellationToken: ct);
        var candidates = await cursor.ToListAsync(ct);

        foreach (var candidate in candidates.Where(p => p.ClientId == clientId))
        {
            var record = candidate.RemovedMeals.FirstOrDefault(r => r.MealId == mealId);

            if (record is not null)
            {
                return (candidate, record.Kind, record.RemovedAtPlanVersion);
            }
        }

        return null;
    }

    /// <summary>
    /// Finds any of the client's own plans (any status) that contains the meal, or null.
    /// </summary>
    private static async Task<NutritionPlan?> FindOwnPlanContainingMealAsync(
        IMongoContext mongo, Guid clientId, Guid mealId, CancellationToken ct)
    {
        var filter = Builders<NutritionPlan>.Filter.Eq(p => p.ClientId, clientId)
                     & Builders<NutritionPlan>.Filter.ElemMatch(
                         p => p.Weeks,
                         Builders<PlanWeek>.Filter.ElemMatch(
                             w => w.Days,
                             Builders<PlanDay>.Filter.ElemMatch(
                                 d => d.Meals,
                                 Builders<PlanMeal>.Filter.Eq(m => m.MealId, mealId))));

        using var cursor = await mongo.NutritionPlans.FindAsync(filter, cancellationToken: ct);
        var candidates = await cursor.ToListAsync(ct);

        return candidates.FirstOrDefault(p => p.ClientId == clientId && CurrentPlanMealResolver.FindMeal(p, mealId) is not null);
    }
}
