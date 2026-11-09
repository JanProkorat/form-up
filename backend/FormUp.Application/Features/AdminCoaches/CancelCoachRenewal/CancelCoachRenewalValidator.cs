using FastEndpoints;
using FluentValidation;
using FormUp.Application.Domain.Constants;

namespace FormUp.Application.Features.AdminCoaches.CancelCoachRenewal;

/// <summary>
/// Validates the <see cref="CancelCoachRenewalRequest"/>.
/// </summary>
internal sealed class CancelCoachRenewalValidator : Validator<CancelCoachRenewalRequest>
{
    /// <summary>Longest accepted reason, so the audit row's values column always fits.</summary>
    public const int MaxReasonLength = 300;

    /// <summary>
    /// Initializes validation rules for cancelling a renewal.
    /// </summary>
    public CancelCoachRenewalValidator()
    {
        RuleFor(x => x.CoachId)
            .NotEmpty().WithErrorCode(ErrorCodes.Required);

        RuleFor(x => x.Reason)
            .NotEmpty().WithErrorCode(ErrorCodes.Required)
            .MaximumLength(MaxReasonLength).WithErrorCode(ErrorCodes.OutOfRange);
    }
}
