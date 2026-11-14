using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.Automations;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Services;

/// <summary>
/// The automation background jobs skip a coach whose plan lacks automations, leave their rules stored, and pick
/// the rules up again once the flag is back on. Each test works on its own coach and drives the jobs with an explicit
/// instant, so nothing waits on the hosted loop and no other coach's data is touched.
/// </summary>
[Collection(TestCollection.Name)]
public class AutomationEntitlementTests(FitnessApiFactory factory)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    // Sunday 23:30 UTC is already Monday 00:30 in Prague, which is where the test clients live.
    private static readonly DateTime SentAt = new(2027, 1, 3, 23, 30, 0, DateTimeKind.Utc);

    private AutomationScheduler Scheduler => factory.Services.GetRequiredService<AutomationScheduler>();

    private async Task<(Actor Coach, Actor Client, long LinkId)> PairAsync()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var linkId = await TestActors.Link(factory, coach, client).CreateAsync(Ct);

        return (coach, client, linkId);
    }

    private Task SetAutomationsAsync(Actor coach, bool enabled) =>
        CoachSubscriptionBuilder.MoveToPlanAsync(factory, coach, plan => plan.CanUseAutomations = enabled);

    private async Task<T> WithDbAsync<T>(Func<ApplicationDbContext, Task<T>> read)
    {
        using var scope = factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    private Task<AutomationSchedule?> ScheduleAsync(long ruleId, long clientProfileId) =>
        WithDbAsync(db => db.AutomationSchedules.AsNoTracking()
            .FirstOrDefaultAsync(s => s.AutomationRuleId == ruleId && s.ClientProfileId == clientProfileId, Ct));

    private Task<List<AutomationRun>> RunsAsync(long ruleId) =>
        WithDbAsync(db => db.AutomationRuns.AsNoTracking().Where(r => r.AutomationRuleId == ruleId).ToListAsync(Ct));

    private Task<bool> RuleExistsAsync(long ruleId) =>
        WithDbAsync(db => db.AutomationRules.AnyAsync(r => r.Id == ruleId, Ct));

    [Fact]
    public async Task Scheduler_CoachWithoutAutomations_ArmsNothingAndKeepsTheRule_ThenResumesWhenFlagIsBack()
    {
        var (coach, client, _) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach);
        await SetAutomationsAsync(coach, enabled: false);

        await Scheduler.RunDueNowAsync(DateTime.UtcNow, Ct);

        (await ScheduleAsync(rule.Id, client.ProfileId)).Should().BeNull("a coach without automations gets no schedule row");
        (await RuleExistsAsync(rule.Id)).Should().BeTrue("the rule stays stored");

        await SetAutomationsAsync(coach, enabled: true);
        await Scheduler.RunDueNowAsync(DateTime.UtcNow, Ct);

        var armed = await ScheduleAsync(rule.Id, client.ProfileId);
        armed.Should().NotBeNull("the rule is picked up again with the flag back on");
        armed!.NextRunAt.Should().NotBeNull();

        await Scheduler.RunDueNowAsync(armed.NextRunAt!.Value.AddMinutes(1), Ct);

        (await RunsAsync(rule.Id)).Should().ContainSingle(run => run.Status == AutomationRunStatus.Sent);
    }

    [Fact]
    public async Task Scheduler_DueRuleOfCoachWhoLostAutomations_SendsNothing()
    {
        var (coach, client, _) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach);
        await Scheduler.RunDueNowAsync(DateTime.UtcNow, Ct);
        var due = (await ScheduleAsync(rule.Id, client.ProfileId))!.NextRunAt!.Value;
        await SetAutomationsAsync(coach, enabled: false);

        await Scheduler.RunDueNowAsync(due.AddMinutes(1), Ct);

        (await RunsAsync(rule.Id)).Should().BeEmpty("nothing is sent while the flag is off");
        (await RuleExistsAsync(rule.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Sender_CoachWithoutAutomations_IsNotEligible_ThenSendsWhenFlagIsBack()
    {
        var (coach, client, _) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach);
        await SetAutomationsAsync(coach, enabled: false);

        var refused = await SendAsync(rule.Id, client.ProfileId, "off", SentAt);

        refused.Should().Be(AutomationSendOutcome.NotEligible);
        (await RunsAsync(rule.Id)).Should().BeEmpty();

        await SetAutomationsAsync(coach, enabled: true);

        (await SendAsync(rule.Id, client.ProfileId, "on", SentAt)).Should().Be(AutomationSendOutcome.Sent);
    }

    [Fact]
    public async Task CheckInSweeper_CoachWithoutAutomations_NotifiesNothing_ThenResumesWhenFlagIsBack()
    {
        var (coach, client, linkId) = await PairAsync();
        var form = await AutomationTestData.InsertQuestionnaireAsync(factory, coach, QuestionnaireType.CheckIn);
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach, r =>
        {
            r.ActionType = AutomationActionType.SendForm;
            r.QuestionnaireId = form.Id;
            r.ScheduleUnit = AutomationScheduleUnit.Weeks;
            r.LocalTime = new TimeOnly(18, 0);
            r.ScheduleWeekday = DayOfWeek.Thursday;
            r.AnswerWindowHours = 48;
            r.ReminderAfterHours = 24;
            r.NotifyCoachOnMissedWeek = true;
        });
        var dueAt = SentAt.AddHours(48);

        await WithDbAsync(async db =>
        {
            db.QuestionnaireResponses.Add(new QuestionnaireResponse
            {
                QuestionnaireId = form.Id,
                ClientId = client.UserId,
                ProfessionalId = coach.UserId,
                LinkId = linkId,
                Status = QuestionnaireResponseStatus.Pending,
                Kind = QuestionnaireResponseKind.CheckIn,
                CheckInWeekStart = new DateOnly(2027, 1, 4),
                CheckInDueAtUtc = dueAt,
            });
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        await SetAutomationsAsync(coach, enabled: false);
        await SweepAsync(coach, dueAt.AddHours(1));

        (await MissedCountAsync(coach)).Should().Be(0, "a coach without automations gets no missed-week notice");
        (await RuleExistsAsync(rule.Id)).Should().BeTrue();

        await SetAutomationsAsync(coach, enabled: true);
        await SweepAsync(coach, dueAt.AddHours(1));

        (await MissedCountAsync(coach)).Should().Be(1);
    }

    private async Task<AutomationSendOutcome> SendAsync(long ruleId, long clientProfileId, string occurrence, DateTime nowUtc)
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AutomationOccurrenceSender>()
            .SendAsync(ruleId, clientProfileId, occurrence, null, nowUtc, Ct);
    }

    private async Task SweepAsync(Actor coach, DateTime nowUtc)
    {
        using var scope = factory.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<AutomationCheckInSweeper>()
            .SweepAsync(nowUtc, Ct, coach.ProfileId);
    }

    private Task<int> MissedCountAsync(Actor coach) =>
        WithDbAsync(db => db.Notifications.CountAsync(
            n => n.RecipientUserId == coach.UserId && n.Type == NotificationType.CoachCheckInMissed, Ct));
}
