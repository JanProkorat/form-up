using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.CoachSubscriptions.GetCoachPlans;

/// <summary>
/// The subscription plans a coach can choose between.
/// </summary>
public class GetCoachPlansResponse
{
    /// <summary>Active plans, cheapest first.</summary>
    public List<CoachPlanDto> Plans { get; set; } = [];
}

/// <summary>
/// One plan a coach can choose: names, current price, client cap and feature flags.
/// </summary>
public class CoachPlanDto
{
    /// <summary>Stable code of the plan.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Czech plan name.</summary>
    public string NameCs { get; set; } = string.Empty;

    /// <summary>English plan name.</summary>
    public string NameEn { get; set; } = string.Empty;

    /// <summary>German plan name.</summary>
    public string NameDe { get; set; } = string.Empty;

    /// <summary>Current price per billing interval, in the smallest unit of <see cref="Currency"/>.</summary>
    public long PriceMinorUnits { get; set; }

    /// <summary>Current price of the second-role add-on in the same unit; null when both roles are included.</summary>
    public long? SecondRoleAddOnPriceMinorUnits { get; set; }

    /// <summary>ISO 4217 currency code.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>How often the plan is billed.</summary>
    public BillingInterval BillingInterval { get; set; }

    /// <summary>Active-client cap; null means unlimited.</summary>
    public int? MaxActiveClients { get; set; }

    /// <summary>True when the plan covers both professional roles at no extra charge.</summary>
    public bool BothRolesIncluded { get; set; }

    /// <summary>Creating, editing and publishing plans.</summary>
    public bool CanCreatePlans { get; set; }

    /// <summary>Messaging clients.</summary>
    public bool CanMessage { get; set; }

    /// <summary>Sending questionnaires and forms.</summary>
    public bool CanSendQuestionnaires { get; set; }

    /// <summary>Weekly check-ins.</summary>
    public bool CanUseWeeklyCheckIns { get; set; }

    /// <summary>Per-client check-in configuration.</summary>
    public bool CanUsePerClientCheckInConfig { get; set; }

    /// <summary>The payments feature.</summary>
    public bool CanUsePayments { get; set; }
}
