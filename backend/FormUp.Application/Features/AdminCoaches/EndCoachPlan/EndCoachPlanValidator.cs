using FastEndpoints;
using FluentValidation;
using FormUp.Application.Domain.Constants;

namespace FormUp.Application.Features.AdminCoaches.EndCoachPlan;

/// <summary>
/// Validates the <see cref="EndCoachPlanRequest"/>.
/// </summary>
internal sealed class EndCoachPlanValidator : Validator<EndCoachPlanRequest>
{
    /// <summary>Longest accepted reason, so the audit row's values column always fits.</summary>
    public const int MaxReasonLength = 300;

    /// <summary>
    /// Initializes validation rules for ending a coach's plan.
    /// </summary>
    public EndCoachPlanValidator()
    {
        RuleFor(x => x.CoachId)
            .NotEmpty().WithErrorCode(ErrorCodes.Required);

        RuleFor(x => x.Reason)
            .NotEmpty().WithErrorCode(ErrorCodes.Required)
            .MaximumLength(MaxReasonLength).WithErrorCode(ErrorCodes.OutOfRange);
    }
}
