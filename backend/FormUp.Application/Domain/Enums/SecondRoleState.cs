namespace FormUp.Application.Domain.Enums;

/// <summary>
/// Whether a coach may hold a second professional role under their current subscription.
/// </summary>
public enum SecondRoleState
{
    /// <summary>The plan covers both roles at no extra charge.</summary>
    Included,

    /// <summary>The plan does not cover both roles and the coach pays for the add-on.</summary>
    PaidAddOn,

    /// <summary>The plan does not cover both roles and the add-on is not paid; a second role is not available.</summary>
    NotAvailable
}
