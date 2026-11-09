using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.AdminCoaches.Shared;

namespace FormUp.Application.Features.AdminCoaches.GetAdminCoach;

/// <summary>
/// One coach as the admin drawer shows them: plan card, clients bar and history. Never carries anything about the
/// coach's clients except counts.
/// </summary>
public class GetAdminCoachResponse
{
    /// <summary>The coach's user id.</summary>
    public Guid CoachId { get; set; }

    /// <summary>The coach's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The coach's email.</summary>
    public string? Email { get; set; }

    /// <summary>Active coach roles (Trainer, Nutritionist).</summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>When the coach joined (UTC).</summary>
    public DateTime JoinedAt { get; set; }

    /// <summary>The plan whose limits apply: the assigned plan while it applies, otherwise the Free plan.</summary>
    public AdminCoachPlanDto Plan { get; set; } = new();

    /// <summary>The coach's status.</summary>
    public AdminCoachStatus Status { get; set; }

    /// <summary>What the coach pays per billing period (plan price plus a paid second-role add-on); null unless paying.</summary>
    public long? PriceMinorUnits { get; set; }

    /// <summary>Currency of <see cref="PriceMinorUnits"/>; null unless paying.</summary>
    public string? Currency { get; set; }

    /// <summary>Billing cadence of <see cref="PriceMinorUnits"/>; null unless paying.</summary>
    public BillingInterval? BillingInterval { get; set; }

    /// <summary>End of the current paid period; null unless the coach is paying.</summary>
    public DateTimeOffset? CurrentPeriodEndsAt { get; set; }

    /// <summary>When the plan was given for free; null unless the gift is in force.</summary>
    public DateTimeOffset? GivenFreeAt { get; set; }

    /// <summary>End of the gift; null unless the gift is in force, and null when it has no end.</summary>
    public DateTimeOffset? GivenFreeUntil { get; set; }

    /// <summary>True when the renewal is cancelled.</summary>
    public bool CancelAtPeriodEnd { get; set; }

    /// <summary>Why the last payment failed. Always null: there is no payment provider yet.</summary>
    public string? FailureDetail { get; set; }

    /// <summary>Active clients: distinct clients with a link that is not ended.</summary>
    public int ClientsUsed { get; set; }

    /// <summary>The plan's client limit; null means unlimited.</summary>
    public int? ClientsMax { get; set; }

    /// <summary>Newest first. Holds the admin and coach actions on the subscription and account, not payments.</summary>
    public List<AdminCoachHistoryEntryDto> History { get; set; } = [];
}

/// <summary>
/// One history entry. Carries no IP address and no raw audit values.
/// </summary>
public class AdminCoachHistoryEntryDto
{
    /// <summary>When it happened (UTC).</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>What happened.</summary>
    public AdminCoachHistoryKind Kind { get; set; }

    /// <summary>True when the coach did it themselves.</summary>
    public bool ByCoach { get; set; }

    /// <summary>Display name of the admin who did it; null for the coach's own actions and for Joined.</summary>
    public string? ActorName { get; set; }

    /// <summary>The admin's reason, when one was given.</summary>
    public string? Reason { get; set; }

    /// <summary>The plan involved, when the entry has one.</summary>
    public string? PlanCode { get; set; }

    /// <summary>The date the entry ends or runs to (gift end, account end, paid period end), when it has one.</summary>
    public DateTimeOffset? Until { get; set; }

    /// <summary>The role involved (Trainer, Nutritionist, Client), for role entries.</summary>
    public string? Role { get; set; }
}
