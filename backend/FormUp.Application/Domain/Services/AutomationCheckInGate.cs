using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// What a coach's subscription lets a rule do at send time: a coach without the automations feature
/// sends nothing (the rules stay stored), a coach without the check-in
/// feature sends no check-ins, and without per-client configuration the client's override and pause
/// no longer apply, so the rule runs on the coach-wide schedule. Other rules are never touched.
/// </summary>
public static class AutomationCheckInGate
{
    /// <summary>Whether the rule sends a check-in form. Needs the questionnaire loaded.</summary>
    /// <param name="rule">The rule, with its questionnaire loaded.</param>
    public static bool IsCheckInRule(AutomationRule rule) =>
        rule is { ActionType: AutomationActionType.SendForm, Questionnaire: { } questionnaire }
        && AutomationQuestionnaireUsability.IsCheckInForm(questionnaire.Type);

    /// <summary>
    /// Whether the coach's entitlements let the rule send at all: automations for every rule, plus the check-in
    /// feature for a check-in rule.
    /// </summary>
    /// <param name="rule">The rule, with its questionnaire loaded.</param>
    /// <param name="entitlements">The coach's entitlements.</param>
    public static bool MaySend(AutomationRule rule, CoachEntitlements entitlements) =>
        entitlements.CanUseAutomations && (!IsCheckInRule(rule) || entitlements.CanUseWeeklyCheckIns);

    /// <summary>The client override that still applies; null for a check-in rule when per-client configuration is off.</summary>
    /// <param name="rule">The rule, with its questionnaire loaded.</param>
    /// <param name="clientOverride">The client's override, if any.</param>
    /// <param name="entitlements">The coach's entitlements.</param>
    public static AutomationClientOverride? ApplicableOverride(
        AutomationRule rule, AutomationClientOverride? clientOverride, CoachEntitlements entitlements) =>
        IsCheckInRule(rule) && !entitlements.CanUsePerClientCheckInConfig ? null : clientOverride;
}
