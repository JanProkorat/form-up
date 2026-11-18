using FastEndpoints;
using FluentValidation;

namespace FormUp.Application.Features.Billing.PreviewPlanChange;

/// <summary>
/// Validates the <see cref="PreviewPlanChangeRequest"/>.
/// </summary>
internal sealed class PreviewPlanChangeValidator : Validator<PreviewPlanChangeRequest>
{
    /// <summary>
    /// Initializes validation rules for previewing a plan change.
    /// </summary>
    public PreviewPlanChangeValidator()
    {
        RuleFor(x => x.PlanCode)
            .NotEmpty()
            .MaximumLength(100);
    }
}
