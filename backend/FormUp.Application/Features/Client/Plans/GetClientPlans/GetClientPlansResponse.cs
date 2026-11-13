namespace FormUp.Application.Features.Client.Plans.GetClientPlans;

/// <summary>
/// Response containing the client's plans.
/// </summary>
public class GetClientPlansResponse
{
    /// <summary>Plan summaries.</summary>
    public List<ClientOwnPlanItem> Items { get; set; } = [];
}

/// <summary>
/// Lightweight plan summary for the client's own plan list. Distinct from
/// the trainer-facing ClientPlanItem in Features/Trainers/ListClientPlans —
/// the two DTOs previously shared a name, which made NSwag's generated
/// client's type-name assignment dependent on document processing order.
/// </summary>
public class ClientOwnPlanItem
{
    /// <summary>Plan public identifier.</summary>
    public Guid PlanId { get; set; }

    /// <summary>Display name.</summary>
    public string PlanName { get; set; } = string.Empty;

    /// <summary>"nutrition" or "training".</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Plan status as string (Active, Completed, Archived).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The Monday when Week 1 begins, if set.</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>Total number of weeks in the plan.</summary>
    public int TotalWeeks { get; set; }

    /// <summary>Number of published weeks.</summary>
    public int PublishedWeekCount { get; set; }

    /// <summary>When this plan was marked as completed.</summary>
    public DateTime? DateCompleted { get; set; }

    /// <summary>Linked questionnaire response ID, if any.</summary>
    public Guid? QuestionnaireResponseId { get; set; }

    /// <summary>
    /// The current week number within this plan, or null if the plan hasn't started yet.
    /// Computed from StartDate (preferred) or the first published week's DatePublished.
    /// </summary>
    public int? CurrentWeek { get; set; }

    /// <summary>
    /// Target daily kilocalories from the plan's GlobalSettings. Populated for nutrition plans only.
    /// </summary>
    public decimal? DailyKcal { get; set; }

    /// <summary>
    /// Mean kilocalories per day, averaged over the published days that have at least one meal.
    /// Nutrition plans only; null for training plans or when no published day has a meal. Whole number.
    /// </summary>
    public decimal? AvgKcalPerDay { get; set; }

    /// <summary>
    /// Mean protein grams per day over the published days that have a meal. Nutrition plans only; null when none. Whole number.
    /// </summary>
    public decimal? AvgProteinPerDay { get; set; }

    /// <summary>
    /// Mean carbohydrate grams per day over the published days that have a meal. Nutrition plans only; null when none. Whole number.
    /// </summary>
    public decimal? AvgCarbsPerDay { get; set; }

    /// <summary>
    /// Mean fat grams per day over the published days that have a meal. Nutrition plans only; null when none. Whole number.
    /// </summary>
    public decimal? AvgFatPerDay { get; set; }

    /// <summary>
    /// Mean fiber grams per day over the published days that have a meal. Nutrition plans only; null when none. Whole number.
    /// </summary>
    public decimal? AvgFiberPerDay { get; set; }

    /// <summary>
    /// Whether today's day-of-week has a session in the current week. Populated for training plans only.
    /// Null when CurrentWeek is null or for nutrition plans.
    /// </summary>
    public bool? HasTodaySession { get; set; }

    /// <summary>
    /// Last day of the plan (StartDate + TotalWeeks weeks - 1 day). Null when the plan has no StartDate.
    /// </summary>
    public DateTime? EndDate { get; set; }

    /// <summary>
    /// Sessions scheduled from StartDate up to today (or EndDate, whichever is earlier).
    /// Training plans only; null without a StartDate or before anything is planned.
    /// </summary>
    public int? SessionsTotal { get; set; }

    /// <summary>
    /// How many of <see cref="SessionsTotal"/> the client completed. Training plans only; never exceeds the total.
    /// </summary>
    public int? SessionsDone { get; set; }

    /// <summary>
    /// Share (0-100) of the meals planned in the window that the client logged as eaten.
    /// Nutrition plans only; null without a StartDate or when no meals are planned yet.
    /// </summary>
    public int? MealsLoggedPercent { get; set; }

    /// <summary>
    /// Full name of the professional who authored the plan. Null when that user no longer exists.
    /// </summary>
    public string? ProfessionalName { get; set; }

    /// <summary>
    /// "Trainer" for training plans, "Nutritionist" for nutrition plans.
    /// </summary>
    public string ProfessionalRole { get; set; } = string.Empty;

    /// <summary>
    /// Last day the published weeks cover. Filled only when the client is over the lapsed coach's Free plan cap; null otherwise.
    /// </summary>
    public DateOnly? RunsUntil { get; set; }
}
