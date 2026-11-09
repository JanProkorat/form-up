using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.AdminCoaches.Shared;

/// <summary>
/// The columns the admin coaches list and detail read for one coach, projected in a single SQL query.
/// Subscription columns are null for a coach with no subscription row.
/// </summary>
internal class AdminCoachRecord
{
    public Guid CoachId { get; set; }

    public long ProfessionalProfileId { get; set; }

    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;

    public string? Email { get; set; }

    public DateTime JoinedAt { get; set; }

    public DateTime? TrainerRoleRemovedAt { get; set; }

    public DateTime? NutritionistRoleRemovedAt { get; set; }

    public SubscriptionStatus? Status { get; set; }

    public DateTimeOffset? FreeUntil { get; set; }

    public DateTimeOffset? GivenFreeAt { get; set; }

    public DateTimeOffset? CurrentPeriodEndsAt { get; set; }

    public bool CancelAtPeriodEnd { get; set; }

    public bool PaysSecondRoleAddOn { get; set; }

    public string? PlanCode { get; set; }

    public string? PlanNameCs { get; set; }

    public string? PlanNameEn { get; set; }

    public string? PlanNameDe { get; set; }

    public int? PlanMaxActiveClients { get; set; }

    public bool PlanBothRolesIncluded { get; set; }

    public BillingInterval? PlanBillingInterval { get; set; }

    public long? PriceMinorUnits { get; set; }

    public long? AddOnMinorUnits { get; set; }

    public string? Currency { get; set; }
}
