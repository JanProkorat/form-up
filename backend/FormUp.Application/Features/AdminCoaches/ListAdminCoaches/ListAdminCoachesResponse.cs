using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.AdminCoaches.Shared;

namespace FormUp.Application.Features.AdminCoaches.ListAdminCoaches;

/// <summary>
/// One page of coaches plus the stats over all coaches.
/// </summary>
public class ListAdminCoachesResponse
{
    /// <summary>Counts and revenue over every coach, independent of the filter, search and page.</summary>
    public AdminCoachStatsDto Stats { get; set; } = new();

    /// <summary>The rows of the requested page.</summary>
    public List<AdminCoachRowDto> Items { get; set; } = [];
}

/// <summary>
/// Counts and revenue over every coach. Paying, Free and Given free add up to All; Payment failed and Cancelling
/// are already part of Paying.
/// </summary>
public class AdminCoachStatsDto
{
    /// <summary>Every coach.</summary>
    public int AllCount { get; set; }

    /// <summary>Coaches paying for a paid plan, including those whose payment failed or whose renewal is cancelled.</summary>
    public int PayingCount { get; set; }

    /// <summary>Paying coaches whose last payment failed.</summary>
    public int PaymentFailedCount { get; set; }

    /// <summary>Paying coaches whose renewal is cancelled.</summary>
    public int CancellingCount { get; set; }

    /// <summary>Coaches on the Free plan or on a plan that no longer applies.</summary>
    public int FreeCount { get; set; }

    /// <summary>Coaches an admin gave a plan for free, gift still in force.</summary>
    public int GivenFreeCount { get; set; }

    /// <summary>Paying coaches per plan.</summary>
    public List<AdminCoachPlanCountDto> PayingByPlan { get; set; } = [];

    /// <summary>
    /// Monthly revenue of the paying coaches, one entry per currency. Each coach counts at the price row they pay
    /// plus the second-role add-on they pay; annual plans count a twelfth. Free, given-free and trialing coaches add nothing.
    /// </summary>
    public List<AdminCoachRevenueDto> MonthlyRevenue { get; set; } = [];
}

/// <summary>
/// Number of paying coaches on a plan.
/// </summary>
public class AdminCoachPlanCountDto
{
    /// <summary>The plan.</summary>
    public AdminCoachPlanDto Plan { get; set; } = new();

    /// <summary>Paying coaches on it.</summary>
    public int Count { get; set; }
}

/// <summary>
/// Monthly revenue in one currency.
/// </summary>
public class AdminCoachRevenueDto
{
    /// <summary>ISO 4217 currency code.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Amount in the smallest unit of the currency.</summary>
    public long AmountMinorUnits { get; set; }
}

/// <summary>
/// One coach row. Never carries anything about the coach's clients except counts.
/// </summary>
public class AdminCoachRowDto
{
    /// <summary>The coach's user id; the route parameter of the coach actions.</summary>
    public Guid CoachId { get; set; }

    /// <summary>The coach's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The coach's email.</summary>
    public string? Email { get; set; }

    /// <summary>Active coach roles (Trainer, Nutritionist); empty once every role was removed.</summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>The plan whose limits apply: the assigned plan while it applies, otherwise the Free plan.</summary>
    public AdminCoachPlanDto Plan { get; set; } = new();

    /// <summary>The coach's status.</summary>
    public AdminCoachStatus Status { get; set; }

    /// <summary>Active clients: distinct clients with a link that is not ended.</summary>
    public int ClientsUsed { get; set; }

    /// <summary>The plan's client limit; null means unlimited.</summary>
    public int? ClientsMax { get; set; }

    /// <summary>Promotion applied to the coach. Always null until promotions exist.</summary>
    public string? Promotion { get; set; }

    /// <summary>End of the current paid period (renewal, or end when cancelling); null unless the coach is paying.</summary>
    public DateTimeOffset? CurrentPeriodEndsAt { get; set; }

    /// <summary>End of the gift; null unless the plan is given free, and null when the gift has no end.</summary>
    public DateTimeOffset? GivenFreeUntil { get; set; }
}
