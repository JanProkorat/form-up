using FormUp.Application.Features.Automations.Shared;

namespace FormUp.Application.Features.Automations.GetAutomationRules;

/// <summary>
/// The caller's global automation rules.
/// </summary>
public class GetAutomationRulesResponse
{
    /// <summary>Global rules, oldest first.</summary>
    public List<AutomationRuleDto> Rules { get; set; } = [];
}
