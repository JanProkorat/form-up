using FormUp.Application.Domain.Documents;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// A client's current Active plan and one of its meals.
/// </summary>
/// <param name="Plan">The Active plan whose date window contains the client-local today.</param>
/// <param name="Meal">The meal inside that plan.</param>
public sealed record CurrentPlanMeal(NutritionPlan Plan, PlanMeal Meal);
