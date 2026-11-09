using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.NutritionPlans.GetPlan;

namespace FormUp.Application.Features.ClientNutrition.GetFullPlan;

/// <summary>
/// Response containing all published weeks of the client's active nutrition plan.
/// </summary>
public class GetFullPlanResponse
{
    /// <summary>External identifier of the nutrition plan.</summary>
    public Guid PlanId { get; set; }

    /// <summary>
    /// Plan <c>Version</c> the client saw; echoed back as <c>BasedOnPlanVersion</c> on offline writes.
    /// </summary>
    public int PlanVersion { get; set; }

    /// <summary>Display name of the nutrition plan.</summary>
    public string PlanName { get; set; } = string.Empty;

    /// <summary>The Monday when Week 1 begins, if set.</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>Global daily nutrition targets.</summary>
    public GlobalNutritionSettings? GlobalSettings { get; set; }

    /// <summary>Published weeks with pre-computed date ranges.</summary>
    public List<FullPlanWeek> Weeks { get; set; } = [];

    /// <summary>Number of published weeks.</summary>
    public int PublishedWeekCount { get; set; }

    /// <summary>Total number of weeks in the plan (including draft).</summary>
    public int TotalWeeks { get; set; }

    /// <summary>
    /// Last day of the plan's last published week, set only when the plan's coach no longer has a plan that covers
    /// this client: after it the plan is read-only. Null otherwise.
    /// </summary>
    public DateOnly? RunsUntil { get; set; }

    /// <summary>Current week number (null if plan is upcoming).</summary>
    public int? CurrentWeek { get; set; }

    /// <summary>Current day of week 1-7 (null if plan is upcoming).</summary>
    public int? CurrentDayOfWeek { get; set; }

    /// <summary>Plan status (Active, Completed, Archived).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Linked questionnaire response public ID, if any.</summary>
    public Guid? QuestionnaireResponseId { get; set; }

    /// <summary>When this plan was marked as completed, if applicable.</summary>
    public DateTime? DateCompleted { get; set; }

    /// <summary>
    /// Set of meal IDs that are fully eaten: every current item of the meal is eaten.
    /// Meal IDs are unique across the plan, so a flat set covers all days.
    /// </summary>
    public HashSet<Guid> EatenMealIds { get; set; } = [];

    /// <summary>
    /// Set of meal IDs that are partly eaten (some, not all, current items). Disjoint from
    /// <see cref="EatenMealIds"/>.
    /// </summary>
    public HashSet<Guid> PartlyEatenMealIds { get; set; } = [];

    /// <summary>
    /// The eaten item ids of each eaten or partly eaten meal, keyed by meal id. Ids of items
    /// no longer in the meal are not listed.
    /// </summary>
    public Dictionary<Guid, List<Guid>> EatenItemIdsByMeal { get; set; } = [];

    /// <summary>
    /// Supplement recommendations for this plan. Clients use this list to configure
    /// local reminder notifications; the list is read-only on the client side.
    /// </summary>
    public List<SupplementDto> Supplements { get; set; } = [];

    /// <summary>
    /// Builds the response from a plan that has at least one published week.
    /// </summary>
    /// <param name="plan">The plan document.</param>
    /// <param name="todayLocalUtc">The client's local calendar day.</param>
    /// <param name="mealLogs">The plan's meal logs merged per meal and day, with derived eaten state.</param>
    public static GetFullPlanResponse FromPlan(NutritionPlan plan, DateTime todayLocalUtc, IReadOnlyCollection<MergedMealLog> mealLogs)
    {
        var eatenMealIds = mealLogs
            .Where(meal => meal.State == MealEatenState.Eaten)
            .Select(meal => meal.MealId)
            .ToHashSet();
        var partlyEatenMealIds = mealLogs
            .Where(meal => meal.State == MealEatenState.PartlyEaten)
            .Select(meal => meal.MealId)
            .Where(mealId => !eatenMealIds.Contains(mealId))
            .ToHashSet();
        var eatenItemIdsByMeal = mealLogs
            .Where(meal => meal.State != MealEatenState.NotEaten)
            .GroupBy(meal => meal.MealId)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(meal => meal.EatenItemIds).Distinct().ToList());

        // Collect published weeks ordered by week number
        var publishedWeeks = plan.Weeks
            .Where(w => w.Status == WeekStatus.Published)
            .OrderBy(w => w.WeekNumber)
            .ToList();

        var today = todayLocalUtc;

        // Anchor date for the per-week start/end dates below. publishedWeeks is non-empty
        // here, which implies StartDate is set (#1015 — see the reachability note below), so the
        // plan-level DatePublished fallback this used to carry was unreachable.
        DateTime? anchorDate = plan.StartDate;

        // Determine currentWeek and currentDayOfWeek
        int? currentWeek = null;
        int? currentDayOfWeek = null;

        if (plan.StartDate.HasValue)
        {
            var daysSinceStart = (int)(today - plan.StartDate.Value.Date).TotalDays;

            if (daysSinceStart < 0)
            {
                // Plan hasn't started yet — upcoming
                currentWeek = null;
                currentDayOfWeek = null;
            }
            else
            {
                var weekNum = daysSinceStart / 7 + 1;
                var dayNum = daysSinceStart % 7 + 1;

                // Check whether the calculated week is published
                var calculatedWeek = plan.Weeks.FirstOrDefault(w => w.WeekNumber == weekNum);

                if (weekNum > publishedWeeks[^1].WeekNumber || calculatedWeek is null || calculatedWeek.Status != WeekStatus.Published)
                {
                    // Beyond published weeks or on an unpublished week — fall back to last published week
                    currentWeek = publishedWeeks[^1].WeekNumber;
                    // Use current day of week (Monday=1 … Sunday=7) — today already carries
                    // the client's local calendar date (#935).
                    var dow = (int)today.DayOfWeek;
                    currentDayOfWeek = dow == 0 ? 7 : dow;
                }
                else
                {
                    currentWeek = weekNum;
                    currentDayOfWeek = dayNum;
                }
            }
        }

        // No else: publishedWeeks is non-empty here, which implies StartDate is set —
        // PublishWeekEndpoint refuses to publish a week without one (START_DATE_REQUIRED) and
        // UpdatePlanEndpoint refuses to clear one while any week is published
        // (START_DATE_LOCKED), and publishing is the only route to WeekStatus.Published. The
        // legacy plan-level DatePublished cycling branch that stood here was unreachable, and
        // the field it read has had no writer since 8d39e113 (#1015).

        // Build week list with pre-computed date ranges
        var fullPlanWeeks = publishedWeeks.Select(w =>
        {
            string weekStart = string.Empty;
            string weekEnd = string.Empty;

            if (anchorDate.HasValue)
            {
                // Week 1 starts on anchorDate; each subsequent week is +7 days
                var weekStartDate = anchorDate.Value.Date.AddDays((w.WeekNumber - 1) * 7);
                var weekEndDate = weekStartDate.AddDays(6);
                weekStart = weekStartDate.ToString("yyyy-MM-dd");
                weekEnd = weekEndDate.ToString("yyyy-MM-dd");
            }

            return new FullPlanWeek
            {
                WeekNumber = w.WeekNumber,
                WeekStartDate = weekStart,
                WeekEndDate = weekEnd,
                Days = w.Days
            };
        }).ToList();

        return new GetFullPlanResponse
        {
            PlanId = plan.ExternalId,
            PlanVersion = plan.Version,
            PlanName = plan.Name,
            StartDate = plan.StartDate,
            GlobalSettings = plan.GlobalSettings,
            Weeks = fullPlanWeeks,
            PublishedWeekCount = publishedWeeks.Count,
            TotalWeeks = plan.Weeks.Count,
            CurrentWeek = currentWeek,
            CurrentDayOfWeek = currentDayOfWeek,
            Status = plan.Status.ToString(),
            QuestionnaireResponseId = plan.QuestionnaireResponseId,
            DateCompleted = plan.DateCompleted,
            EatenMealIds = eatenMealIds,
            PartlyEatenMealIds = partlyEatenMealIds,
            EatenItemIdsByMeal = eatenItemIdsByMeal,
            Supplements = plan.Supplements.Select(s => new SupplementDto
            {
                ExternalId = s.ExternalId,
                Name = s.Name,
                Dose = s.Dose,
                Notes = s.Notes
            }).ToList(),
        };
    }
}

/// <summary>
/// A published week with pre-computed start/end dates.
/// </summary>
public class FullPlanWeek
{
    /// <summary>1-based week number.</summary>
    public int WeekNumber { get; set; }

    /// <summary>ISO date string for the Monday this week starts.</summary>
    public string WeekStartDate { get; set; } = string.Empty;

    /// <summary>ISO date string for the Sunday this week ends.</summary>
    public string WeekEndDate { get; set; } = string.Empty;

    /// <summary>Days in this week.</summary>
    public List<PlanDay> Days { get; set; } = [];
}
