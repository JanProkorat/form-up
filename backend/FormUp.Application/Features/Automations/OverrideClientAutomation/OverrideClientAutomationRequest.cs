using FormUp.Application.Features.Automations.Shared;

namespace FormUp.Application.Features.Automations.OverrideClientAutomation;

/// <summary>
/// Request for customising a global rule for one client; the body is the customised message, trigger and options.
/// </summary>
public class OverrideClientAutomationRequest : AutomationTriggerBody
{
    /// <summary>Route parameter: public id of the client profile.</summary>
    public Guid ClientId { get; set; }

    /// <summary>Route parameter: public id of the rule.</summary>
    public Guid RuleId { get; set; }

    /// <summary>
    /// Public id of the check-in form this client gets instead of the rule's own; null keeps (or returns to) the
    /// rule's form. Only valid on a check-in rule.
    /// </summary>
    public Guid? CheckInQuestionnaireId { get; set; }
}
