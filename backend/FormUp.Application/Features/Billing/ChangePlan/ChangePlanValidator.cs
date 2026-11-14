using FastEndpoints;
using FluentValidation;

namespace FormUp.Application.Features.Billing.ChangePlan;

/// <summary>
/// Validates the <see cref="ChangePlanRequest"/>.
/// </summary>
internal sealed class ChangePlanValidator : Validator<ChangePlanRequest>
{
    /// <summary>
    /// Initializes validation rules for changing plan.
    /// </summary>
    public ChangePlanValidator()
    {
        RuleFor(x => x.PlanCode)
            .NotEmpty()
            .MaximumLength(100);
    }
}
