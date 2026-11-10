using FastEndpoints;
using FluentValidation;

namespace FormUp.Application.Features.CoachSubscriptions.ChooseFreePlan;

/// <summary>
/// Validates the <see cref="ChooseFreePlanRequest"/>.
/// </summary>
internal sealed class ChooseFreePlanValidator : Validator<ChooseFreePlanRequest>
{
    /// <summary>
    /// Initializes validation rules for choosing the Free plan.
    /// </summary>
    public ChooseFreePlanValidator()
    {
        RuleForEach(x => x.KeepClientIds).NotEmpty();
    }
}
