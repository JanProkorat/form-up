namespace FormUp.Application.Domain.Enums;

/// <summary>
/// Where in the web app a coach started a checkout, so the return URL lands on the right page.
/// </summary>
public enum CheckoutFlow
{
    /// <summary>Started from Plan and billing in settings.</summary>
    Settings,

    /// <summary>Started from the plan step of sign-up.</summary>
    SignUp
}
