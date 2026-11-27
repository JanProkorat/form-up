using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Finds a meal in the client's current plan, shared by the meal-eaten endpoints.
/// </summary>
public static class CurrentPlanMealResolver
{
    /// <summary>
    /// Resolves the Active plan whose date window contains today (a client may hold several
    /// sequential plans, #780) and the meal inside it; null when either is missing.
    /// </summary>
    public static async Task<CurrentPlanMeal?> FindCurrentPlanMealAsync(
        IMongoContext mongo, Guid clientId, Guid mealId, DateTime todayLocalUtc, CancellationToken ct)
    {
        var filter = Builders<NutritionPlan>.Filter.And(
            Builders<NutritionPlan>.Filter.Eq(p => p.ClientId, clientId),
            Builders<NutritionPlan>.Filter.Eq(p => p.Status, NutritionPlanStatus.Active));

        using var cursor = await mongo.NutritionPlans.FindAsync(filter, cancellationToken: ct);
        var activePlans = await cursor.ToListAsync(ct);
        var plan = PlanWindowResolver.ResolveCurrentPlan(activePlans, p => p.StartDate, p => p.Weeks.Count, todayLocalUtc);

        var meal = plan is null ? null : FindMeal(plan, mealId);

        return plan is null || meal is null ? null : new CurrentPlanMeal(plan, meal);
    }

    /// <summary>
    /// Finds the meal with the given id anywhere in the plan, or null.
    /// </summary>
    public static PlanMeal? FindMeal(NutritionPlan plan, Guid mealId) =>
        plan.Weeks
            .SelectMany(w => w.Days)
            .SelectMany(d => d.Meals)
            .FirstOrDefault(m => m.MealId == mealId);
}
