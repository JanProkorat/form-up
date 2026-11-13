namespace FormUp.Application.Features.AdminCoaches.Shared;

/// <summary>
/// A subscription plan as an admin sees it on a coach.
/// </summary>
public class AdminCoachPlanDto
{
    /// <summary>Stable plan code, e.g. <c>pro</c>.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Czech plan name.</summary>
    public string NameCs { get; set; } = string.Empty;

    /// <summary>English plan name.</summary>
    public string NameEn { get; set; } = string.Empty;

    /// <summary>German plan name.</summary>
    public string NameDe { get; set; } = string.Empty;
}
