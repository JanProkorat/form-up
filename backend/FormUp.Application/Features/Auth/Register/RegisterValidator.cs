using FastEndpoints;
using FluentValidation;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.Auth.Register;

/// <summary>
/// Validates the <see cref="RegisterRequest"/>.
/// </summary>
public class RegisterValidator : Validator<RegisterRequest>
{
    /// <summary>
    /// Roles that may be self-assigned via the public registration endpoint.
    /// <see cref="UserRole.Admin"/> is intentionally excluded — it can only be
    /// granted out-of-band by a platform administrator.
    /// </summary>
    private static readonly HashSet<string> PubliclyRegistrableRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(UserRole.Trainer),
        nameof(UserRole.Nutritionist),
        nameof(UserRole.Client),
    };

    /// <summary>
    /// Initializes validation rules for user registration.
    /// </summary>
    public RegisterValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(100);

        RuleFor(x => x.InviteToken)
            .MaximumLength(100)
            .When(x => x.InviteToken is not null);

        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(8)
            .MaximumLength(100);

        RuleFor(x => x.ConfirmPassword)
            .NotEmpty()
            .Equal(x => x.Password)
            .WithMessage("Passwords do not match.");

        RuleFor(x => x.FirstName)
            .NotEmpty()
            .MaximumLength(50);

        RuleFor(x => x.LastName)
            .NotEmpty()
            .MaximumLength(50);

        RuleFor(x => x.Roles)
            .NotEmpty()
            .WithMessage("At least one role is required.")
            .Must(roles => !roles.Contains("Client", StringComparer.OrdinalIgnoreCase) ||
                           !roles.Any(r => string.Equals(r, "Trainer", StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(r, "Nutritionist", StringComparison.OrdinalIgnoreCase)))
            .WithMessage("Cannot combine Client role with Trainer or Nutritionist.");

        RuleForEach(x => x.Roles)
            .Must(r => PubliclyRegistrableRoles.Contains(r))
            .WithMessage("Role must be one of: Trainer, Nutritionist, Client.");

        RuleFor(x => x.GdprConsent)
            .Equal(true)
            .WithMessage("GDPR consent is required to register.");

        // Art. 9 health-data consent (HealthDataConsent) is role-conditional:
        //   - Client role: must be true (explicit, affirmative consent required).
        //   - Coach roles (Trainer, Nutritionist): must be null (Art. 9 consent is
        //     not applicable and must not be recorded for coach registrations).

        RuleFor(x => x.HealthDataConsent)
            .Must(v => v == true)
            .When(x => x.Roles.Any(r => string.Equals(r, nameof(UserRole.Client), StringComparison.OrdinalIgnoreCase)))
            .WithMessage("HealthDataConsent must be true for the Client role (GDPR Art. 9).");

        RuleFor(x => x.HealthDataConsent)
            .Must(v => v == null)
            .When(x => x.Roles.Any(r =>
                string.Equals(r, nameof(UserRole.Trainer), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r, nameof(UserRole.Nutritionist), StringComparison.OrdinalIgnoreCase)))
            .WithMessage("HealthDataConsent must be null for Trainer and Nutritionist roles (Art. 9 consent is not applicable to coach registration).");
    }
}
