using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FormUp.Application.Infrastructure.Services;

/// <summary>
/// Finds the sends that fell due for the four events the worker polls for: the client joined, a
/// weekly check-in was missed, the client went inactive, a form was answered. Looks back only a
/// short while, and never to an event that happened before the rule existed.
/// </summary>
/// <param name="db">Application database context, read only.</param>
/// <param name="clientVerdicts">Reads a client's last activity.</param>
/// <param name="scopeFactory">Opens the scope each send runs in.</param>
/// <param name="logger">Logger.</param>
public sealed class AutomationEventPoller(
    IApplicationDbContext db,
    IClientVerdictService clientVerdicts,
    IServiceScopeFactory scopeFactory,
    ILogger<AutomationEventPoller> logger)
{
    private static readonly TimeSpan QuerySlack = TimeSpan.FromDays(2);

    private sealed record Candidate(long RuleId, long ClientProfileId, string Occurrence);

    /// <summary>
    /// Sends every polled-event occurrence of the coach's rules that is due now and has no run yet.
    /// </summary>
    /// <param name="snapshot">The coach's rules and clients.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PollAsync(AutomationCoachSnapshot snapshot, DateTime nowUtc, CancellationToken ct)
    {
        var polled = snapshot.Pairs(nowUtc)
            .Where(p => p.Effective is
            {
                TriggerKind: AutomationTriggerKind.Event,
                Enabled: true,
                IsPaused: false,
            } && AutomationPolledEventCalculator.IsPolled(p.Effective.EventType))
            .ToList();

        if (polled.Count == 0)
        {
            return;
        }

        var candidates = new List<Candidate>();

        foreach (var eventType in polled.Select(p => p.Effective.EventType!.Value).Distinct())
        {
            var pairs = polled.Where(p => p.Effective.EventType == eventType).ToList();

            candidates.AddRange(eventType switch
            {
                AutomationEventType.ClientJoined => FindJoined(pairs, nowUtc),
                AutomationEventType.CheckInMissed => await FindMissedCheckInsAsync(snapshot, pairs, nowUtc, ct),
                AutomationEventType.ClientInactive => await FindInactiveAsync(pairs, nowUtc, ct),
                _ => await FindAnsweredFormsAsync(snapshot, pairs, nowUtc, ct),
            });
        }

        foreach (var candidate in await WithoutRunsAsync(candidates, ct))
        {
            await SendAsync(candidate, nowUtc, ct);
        }
    }

    private static List<Candidate> FindJoined(List<AutomationPair> pairs, DateTime nowUtc)
    {
        var found = new List<Candidate>();

        foreach (var pair in pairs)
        {
            AddIfDue(
                found,
                pair,
                $"joined:{pair.Client.PrimaryLinkId}",
                eventAt: pair.Client.LinkedSince,
                fireFrom: pair.Client.LinkedSince,
                ShiftOf(pair),
                nowUtc);
        }

        return found;
    }

    private async Task<List<Candidate>> FindMissedCheckInsAsync(
        AutomationCoachSnapshot snapshot, List<AutomationPair> pairs, DateTime nowUtc, CancellationToken ct)
    {
        var clientUserIds = pairs.Select(p => p.Client.ClientUserId).Distinct().ToList();
        var coachUserId = snapshot.Coach.UserId;
        var lowerBound = EarliestEventTime(pairs, nowUtc);

        // A check-in is missed once it is past due and still not submitted; the due time is the event time.
        var missed = await db.QuestionnaireResponses
            .AsNoTracking()
            .Where(r => clientUserIds.Contains(r.ClientId)
                        && r.ProfessionalId == coachUserId
                        && r.Kind == QuestionnaireResponseKind.CheckIn
                        && (r.Status == QuestionnaireResponseStatus.Pending || r.Status == QuestionnaireResponseStatus.InProgress)
                        && r.CheckInDueAtUtc != null
                        && r.CheckInDueAtUtc >= lowerBound
                        && r.CheckInDueAtUtc <= nowUtc)
            .Select(r => new { r.PublicId, r.ClientId, DueAt = r.CheckInDueAtUtc!.Value })
            .ToListAsync(ct);

        var found = new List<Candidate>();

        foreach (var pair in pairs)
        {
            foreach (var checkIn in missed.Where(c => c.ClientId == pair.Client.ClientUserId))
            {
                AddIfDue(found, pair, $"checkin:{checkIn.PublicId:N}", checkIn.DueAt, checkIn.DueAt, ShiftOf(pair), nowUtc);
            }
        }

        return found;
    }

    private async Task<List<Candidate>> FindAnsweredFormsAsync(
        AutomationCoachSnapshot snapshot, List<AutomationPair> pairs, DateTime nowUtc, CancellationToken ct)
    {
        var clientUserIds = pairs.Select(p => p.Client.ClientUserId).Distinct().ToList();
        var ruleIds = pairs.Select(p => p.Rule.Id).Distinct().ToList();
        var coachUserId = snapshot.Coach.UserId;
        var lowerBound = EarliestEventTime(pairs, nowUtc);

        var answered = await db.QuestionnaireResponses
            .AsNoTracking()
            .Where(r => clientUserIds.Contains(r.ClientId)
                        && r.ProfessionalId == coachUserId
                        && r.Status == QuestionnaireResponseStatus.Submitted
                        && r.SubmittedAt != null
                        && r.SubmittedAt >= lowerBound
                        && r.SubmittedAt <= nowUtc
                        && r.Questionnaire.Type != QuestionnaireType.CheckIn)
            .Select(r => new { r.PublicId, r.ClientId, SubmittedAt = r.SubmittedAt!.Value })
            .ToListAsync(ct);

        if (answered.Count == 0)
        {
            return [];
        }

        // A response the same rule delivered is its own answer, not an event to react to.
        var delivered = (await db.AutomationRuns
                .AsNoTracking()
                .Where(r => ruleIds.Contains(r.AutomationRuleId) && r.DeliveredEntityId != null)
                .Select(r => new { r.AutomationRuleId, DeliveredEntityId = r.DeliveredEntityId!.Value })
                .ToListAsync(ct))
            .Select(r => (r.AutomationRuleId, r.DeliveredEntityId))
            .ToHashSet();

        var found = new List<Candidate>();

        foreach (var pair in pairs)
        {
            foreach (var response in answered.Where(r => r.ClientId == pair.Client.ClientUserId))
            {
                if (delivered.Contains((pair.Rule.Id, response.PublicId)))
                {
                    continue;
                }

                AddIfDue(found, pair, $"answered:{response.PublicId:N}", response.SubmittedAt, response.SubmittedAt, ShiftOf(pair), nowUtc);
            }
        }

        return found;
    }

    private async Task<List<Candidate>> FindInactiveAsync(List<AutomationPair> pairs, DateTime nowUtc, CancellationToken ct)
    {
        var found = new List<Candidate>();

        // Nobody is inactive for N days before having been linked that long.
        var due = pairs
            .Where(pair => (pair.Effective.EventOffsetDays ?? 0) >= 1
                           && pair.Client.LinkedSince.AddDays(pair.Effective.EventOffsetDays ?? 0) <= nowUtc)
            .ToList();

        if (due.Count == 0)
        {
            return found;
        }

        var lastActive = await clientVerdicts.GetLastActiveAtAsync(
            due.Select(pair => pair.Client).DistinctBy(client => client.ClientUserId)
                .Select(client => (client.ClientUserId, client.ClientProfileId)).ToList(),
            ct);

        foreach (var pair in due)
        {
            var inactiveDays = pair.Effective.EventOffsetDays ?? 0;
            var lastActiveAt = lastActive[pair.Client.ClientUserId];

            var baseline = lastActiveAt is { } active && active > pair.Client.LinkedSince
                ? active
                : pair.Client.LinkedSince;

            // The event is the moment inactivity reaches N days; the send is the first send time after it.
            var inactiveSince = baseline.AddDays(inactiveDays);

            AddIfDue(
                found,
                pair,
                $"inactive:{baseline:yyyyMMddTHHmmss}",
                eventAt: inactiveSince,
                fireFrom: inactiveSince,
                0,
                nowUtc);
        }

        return found;
    }

    private static int ShiftOf(AutomationPair pair) =>
        AutomationPolledEventCalculator.ShiftDays(pair.Effective.EventOffsetDays, pair.Effective.EventOffsetDirection);

    /// <summary>
    /// The earliest event time worth reading for any of the pairs: no earlier than the rule existed,
    /// and no earlier than a send that is still inside the lookback could have come from.
    /// </summary>
    private static DateTime EarliestEventTime(List<AutomationPair> pairs, DateTime nowUtc) =>
        pairs.Min(pair => Max(
            pair.Rule.DateCreated,
            nowUtc - AutomationPolledEventCalculator.Lookback - TimeSpan.FromDays(ShiftOf(pair)) - QuerySlack));

    private static DateTime Max(DateTime first, DateTime second) => first > second ? first : second;

    /// <summary>
    /// Adds the occurrence when its send falls inside the lookback and the event did not predate the rule.
    /// </summary>
    /// <param name="found">The list to add to.</param>
    /// <param name="pair">The rule and client.</param>
    /// <param name="occurrence">Key of the occurrence.</param>
    /// <param name="eventAt">When the event happened (UTC).</param>
    /// <param name="fireFrom">The instant the send's date is counted from.</param>
    /// <param name="shiftDays">Days between <paramref name="fireFrom"/>'s date and the send.</param>
    /// <param name="nowUtc">The current instant.</param>
    private static void AddIfDue(
        List<Candidate> found,
        AutomationPair pair,
        string occurrence,
        DateTime eventAt,
        DateTime fireFrom,
        int shiftDays,
        DateTime nowUtc)
    {
        if (eventAt < pair.Rule.DateCreated)
        {
            return;
        }

        var fireAt = AutomationPolledEventCalculator.ComputeFireAt(
            fireFrom, shiftDays, pair.Effective.LocalTime, pair.Zone);

        if (fireAt > nowUtc || fireAt <= nowUtc - AutomationPolledEventCalculator.Lookback)
        {
            return;
        }

        found.Add(new Candidate(pair.Rule.Id, pair.Client.ClientProfileId, occurrence));
    }

    private async Task<List<Candidate>> WithoutRunsAsync(List<Candidate> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var ruleIds = candidates.Select(c => c.RuleId).Distinct().ToList();
        var occurrences = candidates.Select(c => c.Occurrence).Distinct().ToList();

        var recorded = (await db.AutomationRuns
                .AsNoTracking()
                .Where(r => ruleIds.Contains(r.AutomationRuleId) && occurrences.Contains(r.Occurrence))
                .Select(r => new { r.AutomationRuleId, r.ClientProfileId, r.Occurrence })
                .ToListAsync(ct))
            .Select(r => (r.AutomationRuleId, r.ClientProfileId, r.Occurrence))
            .ToHashSet();

        return candidates
            .DistinctBy(c => (c.RuleId, c.ClientProfileId, c.Occurrence))
            .Where(c => !recorded.Contains((c.RuleId, c.ClientProfileId, c.Occurrence)))
            .ToList();
    }

    private async Task SendAsync(Candidate candidate, DateTime nowUtc, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var sendDb = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var sender = scope.ServiceProvider.GetRequiredService<AutomationOccurrenceSender>();
            var afterCommit = scope.ServiceProvider.GetRequiredService<AutomationAfterCommitQueue>();

            await using var transaction = await sendDb.BeginTransactionAsync(ct);

            // One client at a time, so two workers polling the same event cannot both send it.
            await sendDb.LockClientProfileAsync(candidate.ClientProfileId, ct);

            var outcome = await sender.SendAsync(
                candidate.RuleId, candidate.ClientProfileId, candidate.Occurrence, null, nowUtc, ct);

            if (outcome == AutomationSendOutcome.Duplicate)
            {
                afterCommit.Discard();

                return;
            }

            await transaction.CommitAsync(ct);

            // The send is committed: tell the client now, and never fail it over that.
            await afterCommit.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "AutomationScheduler: sending {Occurrence} of rule {RuleId} to client {ClientProfileId} failed.",
                candidate.Occurrence, candidate.RuleId, candidate.ClientProfileId);

            await RecordFailureAsync(candidate, nowUtc, ct);
        }
    }

    private async Task RecordFailureAsync(Candidate candidate, DateTime nowUtc, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var sendDb = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var sender = scope.ServiceProvider.GetRequiredService<AutomationOccurrenceSender>();

            await using var transaction = await sendDb.BeginTransactionAsync(ct);
            await sender.RecordFailureAsync(candidate.RuleId, candidate.ClientProfileId, candidate.Occurrence, nowUtc, ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "AutomationScheduler: could not record the failed occurrence {Occurrence} of rule {RuleId}.",
                candidate.Occurrence, candidate.RuleId);
        }
    }
}
