using FormUp.Application.Features.Automations.Shared;
using FluentValidation;

namespace FormUp.Application.Features.Automations.OverrideClientAutomation;

/// <summary>
/// Validates the <see cref="OverrideClientAutomationRequest"/>.
/// </summary>
public class OverrideClientAutomationValidator : AutomationTriggerBodyValidator<OverrideClientAutomationRequest>
{
    /// <summary>
    /// Initializes validation rules for customising a rule for a client.
    /// </summary>
    public OverrideClientAutomationValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty();
        RuleFor(x => x.RuleId).NotEmpty();
        RuleFor(x => x.CheckInQuestionnaireId)
            .NotEqual(Guid.Empty)
            .When(x => x.CheckInQuestionnaireId.HasValue);
    }
}
