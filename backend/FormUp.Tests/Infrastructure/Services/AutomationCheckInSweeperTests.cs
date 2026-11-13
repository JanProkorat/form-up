using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.Automations;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Infrastructure.Services;

/// <summary>
/// Integration tests for how a check-in rule delivers its form and for <see cref="AutomationCheckInSweeper"/>.
/// Each test works on its own coach's rule; the sweeper is limited to that coach, so no other test's data is touched.
/// </summary>
[Collection(TestCollection.Name)]
public class AutomationCheckInSweeperTests(FitnessApiFactory factory)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    // Sunday 23:30 UTC is already Monday 00:30 in Prague, which is where the test clients live.
    private static readonly DateTime SentAt = new(2027, 1, 3, 23, 30, 0, DateTimeKind.Utc);

    private sealed record Fixture(Actor Coach, Actor Client, long LinkId, Questionnaire Form, AutomationRule Rule);

    private async Task<Fixture> ArrangeAsync(Action<AutomationRule>? configure = null)
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "studio");
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var linkId = await TestActors.Link(factory, coach, client).CreateAsync(Ct);
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
            r.SkipWhilePreviousUnanswered = true;
            configure?.Invoke(r);
        });

        return new Fixture(coach, client, linkId, form, rule);
    }

    private async Task<T> WithDbAsync<T>(Func<ApplicationDbContext, Task<T>> read)
    {
        using var scope = factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    private async Task<AutomationSendOutcome> SendAsync(Fixture fixture, string occurrence, DateTime nowUtc)
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AutomationOccurrenceSender>()
            .SendAsync(fixture.Rule.Id, fixture.Client.ProfileId, occurrence, null, nowUtc, Ct);
    }

    private async Task SweepAsync(Fixture fixture, DateTime nowUtc)
    {
        using var scope = factory.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<AutomationCheckInSweeper>()
            .SweepAsync(nowUtc, Ct, fixture.Coach.ProfileId);
    }

    private async Task<QuestionnaireResponse> AddOpenCheckInAsync(Fixture fixture, DateTime dueAt) =>
        await WithDbAsync(async db =>
        {
            var response = new QuestionnaireResponse
            {
                QuestionnaireId = fixture.Form.Id,
                ClientId = fixture.Client.UserId,
                ProfessionalId = fixture.Coach.UserId,
                LinkId = fixture.LinkId,
                Status = QuestionnaireResponseStatus.Pending,
                Kind = QuestionnaireResponseKind.CheckIn,
                CheckInWeekStart = new DateOnly(2027, 1, 4),
                CheckInDueAtUtc = dueAt,
            };
            db.QuestionnaireResponses.Add(response);
            await db.SaveChangesAsync(Ct);

            return response;
        });

    private List<FakeRealtimeNotifier.NotifyCall> MissedEventsFor(Guid coachUserId) =>
        factory.Services.GetRequiredService<FakeRealtimeNotifier>().Calls
            .Where(call => call.UserId == coachUserId && call.EventType == "checkinmissed")
            .ToList();

    private Task<int> NotificationCountAsync(Guid userId, NotificationType type) =>
        WithDbAsync(db => db.Notifications.CountAsync(n => n.RecipientUserId == userId && n.Type == type, Ct));

    [Fact]
    public async Task Send_CheckInRule_StampsKindWeekAndDueTimeAndNeverSkips()
    {
        var fixture = await ArrangeAsync();

        (await SendAsync(fixture, "first", SentAt)).Should().Be(AutomationSendOutcome.Sent);
        (await SendAsync(fixture, "second", SentAt.AddDays(7)))
            .Should().Be(AutomationSendOutcome.Sent, "a check-in never waits for the previous one to be answered");

        var responses = await WithDbAsync(db => db.QuestionnaireResponses.AsNoTracking()
            .Where(r => r.QuestionnaireId == fixture.Form.Id && r.ClientId == fixture.Client.UserId)
            .OrderBy(r => r.Id)
            .ToListAsync(Ct));
        responses.Should().HaveCount(2);
        responses[0].Kind.Should().Be(QuestionnaireResponseKind.CheckIn);
        responses[0].CheckInWeekStart.Should().Be(new DateOnly(2027, 1, 4), "the client's local Monday, not the UTC date");
        responses[0].CheckInDueAtUtc.Should().Be(SentAt.AddHours(48));
        responses[1].CheckInWeekStart.Should().Be(new DateOnly(2027, 1, 11));
    }

    [Fact]
    public async Task Send_SevenDayWindow_NextWeekStillSendsAndTheUnansweredOneEndsUpMissed()
    {
        var fixture = await ArrangeAsync(r =>
        {
            r.AnswerWindowHours = 168;
            r.ReminderAfterHours = 24;
        });

        (await SendAsync(fixture, "first", SentAt)).Should().Be(AutomationSendOutcome.Sent);
        (await SendAsync(fixture, "second", SentAt.AddDays(7))).Should().Be(AutomationSendOutcome.Sent);
        await SweepAsync(fixture, SentAt.AddDays(7).AddHours(1));

        var responses = await WithDbAsync(db => db.QuestionnaireResponses.AsNoTracking()
            .Where(r => r.QuestionnaireId == fixture.Form.Id && r.ClientId == fixture.Client.UserId)
            .OrderBy(r => r.Id)
            .ToListAsync(Ct));
        responses.Should().HaveCount(2);
        responses[0].CheckInDueAtUtc.Should().Be(SentAt.AddHours(168));
        responses[0].CheckInMissedNotifiedAtUtc.Should().NotBeNull("the unanswered first week is missed once its window ends");
        responses[1].CheckInMissedNotifiedAtUtc.Should().BeNull("the second week is still open");
        (await NotificationCountAsync(fixture.Coach.UserId, NotificationType.CoachCheckInMissed)).Should().Be(1);
    }

    [Fact]
    public async Task Send_OneDayWindow_DueAfter24HoursAndRemindedAfter12()
    {
        var fixture = await ArrangeAsync(r =>
        {
            r.AnswerWindowHours = 24;
            r.ReminderAfterHours = 12;
        });
        await SendAsync(fixture, "first", SentAt);

        var response = await WithDbAsync(db => db.QuestionnaireResponses.AsNoTracking()
            .SingleAsync(r => r.QuestionnaireId == fixture.Form.Id && r.ClientId == fixture.Client.UserId, Ct));
        response.CheckInDueAtUtc.Should().Be(SentAt.AddHours(24));

        await SweepAsync(fixture, SentAt.AddHours(11));
        (await NotificationCountAsync(fixture.Client.UserId, NotificationType.ClientCheckInReminder)).Should().Be(0, "12 h have not passed");

        await SweepAsync(fixture, SentAt.AddHours(13));
        (await NotificationCountAsync(fixture.Client.UserId, NotificationType.ClientCheckInReminder)).Should().Be(1);
    }

    [Fact]
    public async Task Send_CheckInRuleOfACoachWithoutTheCheckInFeature_SendsNothing()
    {
        var fixture = await ArrangeAsync();
        await CoachSubscriptionBuilder.SubscribeAsync(
            factory, fixture.Coach, canUseWeeklyCheckIns: false, canUsePerClientCheckInConfig: false);

        (await SendAsync(fixture, "first", SentAt)).Should().Be(AutomationSendOutcome.NotEligible);

        (await WithDbAsync(db => db.QuestionnaireResponses.CountAsync(r => r.QuestionnaireId == fixture.Form.Id, Ct)))
            .Should().Be(0, "a coach whose plan has no check-ins sends none");
    }

    [Fact]
    public async Task Send_OnboardingFormRule_KeepsKindQuestionnaireAndNoCheckInFields()
    {
        var plainForm = await ArrangeAsync();
        var coach = plainForm.Coach;
        var form = await AutomationTestData.InsertQuestionnaireAsync(factory, coach);
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach, r =>
        {
            r.ActionType = AutomationActionType.SendForm;
            r.QuestionnaireId = form.Id;
        });

        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AutomationOccurrenceSender>()
            .SendAsync(rule.Id, plainForm.Client.ProfileId, "plain", null, SentAt, Ct);

        var response = await WithDbAsync(db => db.QuestionnaireResponses.AsNoTracking()
            .SingleAsync(r => r.QuestionnaireId == form.Id, Ct));
        response.Kind.Should().Be(QuestionnaireResponseKind.Questionnaire);
        response.CheckInWeekStart.Should().BeNull();
        response.CheckInDueAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Sweep_UnansweredCheckIn_RemindsTheClientOnceOverTwoSweeps()
    {
        var fixture = await ArrangeAsync();
        var response = await AddOpenCheckInAsync(fixture, SentAt.AddHours(48));

        await SweepAsync(fixture, SentAt.AddHours(23));
        (await NotificationCountAsync(fixture.Client.UserId, NotificationType.ClientCheckInReminder)).Should().Be(0, "24 h have not passed");

        await SweepAsync(fixture, SentAt.AddHours(25));
        await SweepAsync(fixture, SentAt.AddHours(26));

        (await NotificationCountAsync(fixture.Client.UserId, NotificationType.ClientCheckInReminder)).Should().Be(1);
        var stored = await WithDbAsync(db => db.QuestionnaireResponses.AsNoTracking().SingleAsync(r => r.Id == response.Id, Ct));
        stored.CheckInReminderSentAtUtc.Should().Be(SentAt.AddHours(25));
    }

    [Fact]
    public async Task Sweep_MissedWeek_NotifiesTheCoachOnceOverTwoSweeps()
    {
        var fixture = await ArrangeAsync();
        var response = await AddOpenCheckInAsync(fixture, SentAt.AddHours(48));

        await SweepAsync(fixture, SentAt.AddHours(49));
        await SweepAsync(fixture, SentAt.AddHours(50));

        (await NotificationCountAsync(fixture.Coach.UserId, NotificationType.CoachCheckInMissed)).Should().Be(1);
        (await NotificationCountAsync(fixture.Client.UserId, NotificationType.ClientCheckInReminder))
            .Should().Be(0, "a week that is already missed is not reminded");

        var events = MissedEventsFor(fixture.Coach.UserId);
        events.Should().ContainSingle("the live alert goes out once, like the stored notice");
        var payload = JsonSerializer.SerializeToElement(events[0].Payload);
        payload.GetProperty("ClientPublicId").GetGuid().Should().Be(fixture.Client.PublicId);
        payload.GetProperty("ResponsePublicId").GetGuid().Should().Be(response.PublicId);
    }

    [Fact]
    public async Task Sweep_MissedWeekWhenTheHubFails_StillStampsAndNotifies()
    {
        var fixture = await ArrangeAsync();
        var response = await AddOpenCheckInAsync(fixture, SentAt.AddHours(48));
        factory.Services.GetRequiredService<FakeRealtimeNotifier>().SimulateThrowOnNextCall();

        await SweepAsync(fixture, SentAt.AddHours(49));

        var stored = await WithDbAsync(db => db.QuestionnaireResponses.AsNoTracking().SingleAsync(r => r.Id == response.Id, Ct));
        stored.CheckInMissedNotifiedAtUtc.Should().NotBeNull();
        (await NotificationCountAsync(fixture.Coach.UserId, NotificationType.CoachCheckInMissed)).Should().Be(1);
    }

    [Fact]
    public async Task Sweep_EndedCollaboration_SendsNeitherReminderNorMissedNotice()
    {
        var fixture = await ArrangeAsync();
        var reminded = await AddOpenCheckInAsync(fixture, SentAt.AddHours(48));
        await WithDbAsync(db => db.ClientProfessionalLinks.Where(l => l.Id == fixture.LinkId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ConnectionState, ConnectionState.Ended), Ct));

        await SweepAsync(fixture, SentAt.AddHours(25));
        await SweepAsync(fixture, SentAt.AddHours(60));

        (await NotificationCountAsync(fixture.Client.UserId, NotificationType.ClientCheckInReminder)).Should().Be(0);
        (await NotificationCountAsync(fixture.Coach.UserId, NotificationType.CoachCheckInMissed)).Should().Be(0);
        var stored = await WithDbAsync(db => db.QuestionnaireResponses.AsNoTracking().SingleAsync(r => r.Id == reminded.Id, Ct));
        stored.CheckInReminderSentAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Sweep_NotifyOffAndNoReminder_SendsNothing()
    {
        var fixture = await ArrangeAsync(r =>
        {
            r.NotifyCoachOnMissedWeek = false;
            r.ReminderAfterHours = null;
        });
        await AddOpenCheckInAsync(fixture, SentAt.AddHours(48));

        await SweepAsync(fixture, SentAt.AddHours(30));
        await SweepAsync(fixture, SentAt.AddHours(60));

        (await NotificationCountAsync(fixture.Coach.UserId, NotificationType.CoachCheckInMissed)).Should().Be(0);
        (await NotificationCountAsync(fixture.Client.UserId, NotificationType.ClientCheckInReminder)).Should().Be(0);
        MissedEventsFor(fixture.Coach.UserId).Should().BeEmpty();
    }
}
