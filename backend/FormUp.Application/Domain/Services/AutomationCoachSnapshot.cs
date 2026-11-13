using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// One coach's automation state, loaded together so the worker can walk every (rule, client)
/// pair without a query per pair. Read-only: nothing here is tracked.
/// </summary>
public sealed class AutomationCoachSnapshot
{
    private readonly IReadOnlyDictionary<(long RuleId, long ClientId), AutomationClientOverride> _overrides;
    private readonly IReadOnlyDictionary<(long RuleId, long ClientId), int> _sentCounts;
    private readonly IReadOnlyDictionary<Guid, List<AutomationPlanWindow>> _planWindows;
    private readonly CoachEntitlements _entitlements;

    private AutomationCoachSnapshot(
        ProfessionalProfile coach,
        IReadOnlyList<AutomationRule> rules,
        IReadOnlyList<AutomationCoveredClient> clients,
        IReadOnlyDictionary<(long RuleId, long ClientId), AutomationClientOverride> overrides,
        IReadOnlyDictionary<(long RuleId, long ClientId), int> sentCounts,
        IReadOnlyDictionary<Guid, List<AutomationPlanWindow>> planWindows,
        CoachEntitlements entitlements)
    {
        Coach = coach;
        Rules = rules;
        Clients = clients;
        _overrides = overrides;
        _sentCounts = sentCounts;
        _planWindows = planWindows;
        _entitlements = entitlements;
    }

    /// <summary>The coach's professional profile, with their user loaded.</summary>
    public ProfessionalProfile Coach { get; }

    /// <summary>The coach's rules, each with its audience clients.</summary>
    public IReadOnlyList<AutomationRule> Rules { get; }

    /// <summary>The clients the coach's automations may reach.</summary>
    public IReadOnlyList<AutomationCoveredClient> Clients { get; }

    /// <summary>How many requests the rule has sent the client so far; 0 when the limit does not matter.</summary>
    public int SentCount(long ruleId, long clientProfileId) =>
        _sentCounts.GetValueOrDefault((ruleId, clientProfileId));

    /// <summary>The coach's plan windows for a client; empty when none is read.</summary>
    public IReadOnlyList<AutomationPlanWindow> PlanWindows(Guid clientUserId) =>
        _planWindows.TryGetValue(clientUserId, out var windows) ? windows : [];

    /// <summary>
    /// Every rule that applies to a covered client, resolved against the client's override and pause.
    /// </summary>
    /// <param name="nowUtc">The current instant, which decides whether a pause lapsed.</param>
    public IEnumerable<AutomationPair> Pairs(DateTime nowUtc)
    {
        foreach (var client in Clients)
        {
            var zone = ClientLocalDateResolver.ResolveTimeZone(client.TimeZoneId);
            var localToday = ClientLocalDateResolver.ResolveLocalDate(nowUtc, zone);
            var startLocalDate = AutomationNextSendCalculator.ResolveStartLocalDate(client.LinkedSince, zone);

            foreach (var rule in Rules)
            {
                if (!AutomationAudienceFilter.Covers(rule, client.ClientProfileId, client.TagIds)
                    || !AutomationCheckInGate.MaySend(rule, _entitlements))
                {
                    continue;
                }

                var clientOverride = AutomationCheckInGate.ApplicableOverride(
                    rule, _overrides.GetValueOrDefault((rule.Id, client.ClientProfileId)), _entitlements);
                var effective = AutomationEffectiveRule.Resolve(rule, clientOverride, localToday);

                yield return new AutomationPair(
                    rule,
                    client,
                    effective,
                    zone,
                    startLocalDate,
                    AutomationNextSendCalculator.ResolveFirstSendNotBefore(client.CollaborationStartedAtUtc, rule, clientOverride));
            }
        }
    }

    /// <summary>
    /// Loads a coach's snapshot, or null when the coach has no rules or their profile is gone.
    /// </summary>
    /// <param name="db">Application database context.</param>
    /// <param name="mongo">MongoDB context, read for plan windows.</param>
    /// <param name="entitlementService">Resolves the coach's subscription entitlements.</param>
    /// <param name="coachProfileId">The coach's professional profile id.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<AutomationCoachSnapshot?> LoadAsync(
        IApplicationDbContext db,
        IMongoContext mongo,
        EntitlementService entitlementService,
        long coachProfileId,
        CancellationToken ct)
    {
        var coach = await db.ProfessionalProfiles
            .AsNoTracking()
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.Id == coachProfileId, ct);

        var rules = await db.AutomationRules
            .AsNoTracking()
            .Include(r => r.AudienceClients)
            .Include(r => r.Questionnaire)
            .Where(r => r.OwnerProfessionalProfileId == coachProfileId)
            .OrderBy(r => r.Id)
            .AsSplitQuery()
            .ToListAsync(ct);

        if (coach is null || rules.Count == 0)
        {
            return null;
        }

        var clients = await AutomationCoverage.LoadCoveredClientsAsync(db, coachProfileId, ct, excludeRemovedRoles: true);
        var ruleIds = rules.Select(r => r.Id).ToList();

        var overrides = await db.AutomationClientOverrides
            .AsNoTracking()
            .Where(o => ruleIds.Contains(o.AutomationRuleId))
            .ToDictionaryAsync(o => (o.AutomationRuleId, o.ClientProfileId), ct);

        var limitedRuleIds = rules
            .Where(r => r.StopAfterSends != null)
            .Select(r => r.Id)
            .Concat(overrides.Values.Where(o => o.IsCustomized && o.StopAfterSends != null).Select(o => o.AutomationRuleId))
            .Distinct()
            .ToList();

        var sentCounts = limitedRuleIds.Count == 0
            ? new Dictionary<(long RuleId, long ClientId), int>()
            : (await db.AutomationRuns
                .AsNoTracking()
                .Where(r => limitedRuleIds.Contains(r.AutomationRuleId) && r.Status == AutomationRunStatus.Sent)
                .GroupBy(r => new { r.AutomationRuleId, r.ClientProfileId })
                .Select(g => new { g.Key.AutomationRuleId, g.Key.ClientProfileId, Count = g.Count() })
                .ToListAsync(ct))
                .ToDictionary(g => (g.AutomationRuleId, g.ClientProfileId), g => g.Count);

        var needsPlans = rules.Any(r => IsPlanEventTrigger(r.TriggerKind, r.EventType))
                         || overrides.Values.Any(o => o.IsCustomized && IsPlanEventTrigger(o.TriggerKind, o.EventType));

        var planWindows = needsPlans
            ? await AutomationPlanWindowLoader.LoadAsync(
                mongo,
                coach.UserId,
                clients.Where(c => c.CanViewTrainingPlans).Select(c => c.ClientUserId).ToList(),
                clients.Where(c => c.CanViewNutritionPlans).Select(c => c.ClientUserId).ToList(),
                ct)
            : new Dictionary<Guid, List<AutomationPlanWindow>>();

        var entitlements = await entitlementService.GetEntitlementsAsync(coachProfileId, ct);

        return new AutomationCoachSnapshot(coach, rules, clients, overrides, sentCounts, planWindows, entitlements);
    }

    private static bool IsPlanEventTrigger(AutomationTriggerKind kind, AutomationEventType? eventType) =>
        kind == AutomationTriggerKind.Event && eventType is AutomationEventType.PlanStarted or AutomationEventType.PlanEnded;
}
