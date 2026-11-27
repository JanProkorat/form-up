namespace FormUp.Application.Features.Auth.Register;

/// <summary>
/// Request model for user registration.
/// </summary>
public class RegisterRequest
{
    /// <summary>
    /// User's email address (used as login).
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// User's chosen password.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Password confirmation (must match Password).
    /// </summary>
    public string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>
    /// User's first name.
    /// </summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>
    /// User's last name.
    /// </summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// The roles the user is registering as. Must contain at least one of: Trainer, Nutritionist, Client.
    /// </summary>
    public List<string> Roles { get; set; } = new();

    /// <summary>
    /// Explicit GDPR consent for personal data processing (Art. 6 GDPR). Required for all roles.
    /// </summary>
    public bool GdprConsent { get; set; }

    /// <summary>
    /// Explicit consent for processing health data under GDPR Art. 9.
    /// Must be true for the Client role; must be null for Trainer and Nutritionist roles.
    /// </summary>
    public bool? HealthDataConsent { get; set; }

    /// <summary>
    /// Optional invitation token from an invite link. When valid, the email and Client role are bound to the invite.
    /// </summary>
    public string? InviteToken { get; set; }
}
