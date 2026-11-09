using FastEndpoints;
using FluentValidation;
using FormUp.Application.Domain.Constants;

namespace FormUp.Application.Features.AdminCoaches.DisableCoachAccountByAdmin;

/// <summary>
/// Validates the <see cref="DisableCoachAccountByAdminRequest"/>.
/// </summary>
internal sealed class DisableCoachAccountByAdminValidator : Validator<DisableCoachAccountByAdminRequest>
{
    /// <summary>Longest accepted reason, so the audit row's values column always fits.</summary>
    public const int MaxReasonLength = 300;

    /// <summary>
    /// Initializes validation rules for disabling a coach account as an admin.
    /// </summary>
    public DisableCoachAccountByAdminValidator()
    {
        RuleFor(x => x.CoachId)
            .NotEmpty().WithErrorCode(ErrorCodes.Required);

        RuleFor(x => x.Reason)
            .NotEmpty().WithErrorCode(ErrorCodes.Required)
            .MaximumLength(MaxReasonLength).WithErrorCode(ErrorCodes.OutOfRange);
    }
}
