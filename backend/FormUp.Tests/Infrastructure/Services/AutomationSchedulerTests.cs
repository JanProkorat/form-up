using System.Net.Http.Json;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Automations.GetClientAutomations;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.Automations;
using FormUp.Tests.Endpoints.SessionExecutions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Infrastructure.Services;

/// <summary>
/// Integration tests for <see cref="AutomationScheduler"/>. They call <c>RunDueNowAsync</c> with an
/// explicit instant, never relying on the hosted loop, and read plan data from the real Mongo container.
/// </summary>
[Collection(TestCollection.Name)]
public class AutomationSchedulerTests(FitnessApiFactory factory) : IAsyncLifetime
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    /// <inheritdoc />
    public async ValueTask InitializeAsync() => await ClearAutomationRulesAsync();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // The scheduler sweeps every coach's rules, so rules left by earlier tests slow each run.
    private async Task ClearAutomationRulesAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.AutomationRules.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }

    private AutomationScheduler Scheduler => factory.Services.GetRequiredService<AutomationScheduler>();

    private async Task<(Actor Coach, Actor Client, long LinkId)> PairAsync(
        Func<LinkBuilder, LinkBuilder>? link = null, Func<ActorBuilder, ActorBuilder>? coach = null)
    {
        var coachActor = await (coach?.Invoke(TestActors.Trainer(factory)) ?? TestActors.Trainer(factory)).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var builder = TestActors.Link(factory, coachActor, client);
        var linkId = await (link?.Invoke(builder) ?? builder).CreateAsync(Ct);

        return (coachActor, client, linkId);
    }

    private async Task<T> WithDbAsync<T>(Func<ApplicationDbContext, Task<T>> read)
    {
        using var scope = factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    private Task<List<AutomationRun>> RunsAsync(long ruleId) =>
        WithDbAsync(db => db.AutomationRuns.AsNoTracking().Where(r => r.AutomationRuleId == ruleId).OrderBy(r => r.Id).ToListAsync(Ct));

    private Task<AutomationSchedule?> ScheduleAsync(long ruleId, long clientProfileId) =>
        WithDbAsync(db => db.AutomationSchedules.AsNoTracking()
            .FirstOrDefaultAsync(s => s.AutomationRuleId == ruleId && s.ClientProfileId == clientProfileId, Ct));

    private Task<int> NotificationCountAsync(Guid userId, NotificationType type) =>
        WithDbAsync(db => db.Notifications.CountAsync(n => n.RecipientUserId == userId && n.Type == type, Ct));

    /// <summary>Arms the rule's schedule row with a tick at the real clock and returns its due instant.</summary>
    private async Task<DateTime> ArmAsync(AutomationRule rule, Actor client)
    {
        await Scheduler.RunDueNowAsync(DateTime.UtcNow, Ct);

        var row = await ScheduleAsync(rule.Id, client.ProfileId);

        row.Should().NotBeNull("the reconcile arms a row for a covered client");
        row!.NextRunAt.Should().NotBeNull();

        return row.NextRunAt!.Value;
    }

    private static DateTime After(DateTime due) => due.AddMinutes(1);

    // ── reconcile ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Reconcile_ScheduleRule_StoresTheNextSendTheClientTabShows()
    {
        var (coach, client, _) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach);

        await Scheduler.RunDueNowAsync(DateTime.UtcNow, Ct);

        var row = await ScheduleAsync(rule.Id, client.ProfileId);
        var shown = (await ListAsync(coach, client)).Items.Single(i => i.RuleId == rule.PublicId).NextSendAt;

        row.Should().NotBeNull();
        row!.NextRunAt.Should().BeAfter(DateTime.UtcNow);
        row.TimeZoneId.Should().Be("Europe/Prague");
        row.LocalTime.Should().Be(new TimeOnly(9, 0));
        shown.Should().NotBeNull();
        shown!.Value.Should().BeCloseTo(row.NextRunAt!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Reconcile_PlanEventRule_StoresTheNextPlanSendTheClientTabShows()
    {
        var (coach, client, _) = await PairAsync();
        await AutomationTestData.SeedTrainingPlanAsync(factory, client.UserId, coach.UserId, DateTime.UtcNow.Date.AddDays(10), 4);
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach, r =>
        {
            r.TriggerKind = AutomationTriggerKind.Event;
            r.EventType = AutomationEventType.PlanStarted;
            r.EventOffsetDays = 1;
            r.EventOffsetDirection = AutomationOffsetDirection.After;
        });

        await Scheduler.RunDueNowAsync(DateTime.UtcNow, Ct);

        var row = await ScheduleAsync(rule.Id, client.ProfileId);
        var shown = (await ListAsync(coach, client)).Items.Single(i => i.RuleId == rule.PublicId).NextSendAt;

        row.Should().NotBeNull();
        shown!.Value.Should().BeCloseTo(row!.NextRunAt!.Value, TimeSpan.FromSeconds(1));
        row.LocalTime.Should().Be(AutomationPlanEventCalculator.DefaultLocalTime);
    }

    [Fact]
    public async Task Reconcile_ClientLinkedLongAgo_NeverFiresRetroactively()
    {
        var (coach, client, linkId) = await PairAsync();
        await AutomationTestData.SetLinkCreatedAsync(factory, linkId, DateTime.UtcNow.AddDays(-40));
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach);

        await Scheduler.RunDueNowAsync(DateTime.UtcNow, Ct);

        (await RunsAsync(rule.Id)).Should().BeEmpty();
        (await ScheduleAsync(rule.Id, client.ProfileId))!.NextRunAt.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task Reconcile_PausedClient_HasNoNextRunAndNoSkippedRun()
    {
        var (coach, client, _) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach);
        await PauseAsync(rule, client);

        await Scheduler.RunDueNowAsync(DateTime.UtcNow, Ct);
        await Scheduler.RunDueNowAsync(DateTime.UtcNow.AddDays(3), Ct);

        (await ScheduleAsync(rule.Id, client.ProfileId))?.NextRunAt.Should().BeNull();
        (await RunsAsync(rule.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_PauseLapses_ArmsTheRowAgain()
    {
        var (coach, client, _) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach);
        await PauseAsync(rule, client, pausedUntil: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)));

        await Scheduler.RunDueNowAsync(DateTime.UtcNow, Ct);

        (await ScheduleAsync(rule.Id, client.ProfileId))!.NextRunAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Reconcile_RuleDisabled_StoresNoNextRun()
    {
        var (coach, client, _) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach, r => r.Enabled = false);

        await Scheduler.RunDueNowAsync(DateTime.UtcNow, Ct);

        (await ScheduleAsync(rule.Id, client.ProfileId)).Should().BeNull();
    }

    [Fact]
    public async Task Reconcile_CompletedPlan_DropsOutOfPlanEvents()
    {
        var (coach, client, _) = await PairAsync();
        await AutomationTestData.SeedTrainingPlanAsync(
            factory, client.UserId, coach.UserId, DateTime.UtcNow.Date.AddDays(5), 4, TrainingPlanStatus.Completed);
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach, r =>
        {
            r.TriggerKind = AutomationTriggerKind.Event;
            r.EventType = AutomationEventType.PlanStarted;
            r.EventOffsetDays = 0;
        });

        await Scheduler.RunDueNowAsync(DateTime.UtcNow, Ct);
        await Scheduler.RunDueNowAsync(DateTime.UtcNow.AddDays(6), Ct);

        (await ScheduleAsync(rule.Id, client.ProfileId)).Should().BeNull();
        (await RunsAsync(rule.Id)).Should().BeEmpty();
    }

    // ── claim and send ──────────────────────────────────────────────────────

    [Fact]
    public async Task Run_DueMeasurementRule_NotifiesTheClientAndRecordsTheRun()
    {
        var (coach, client, _) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach);
        var due = await ArmAsync(rule, client);
        var push = factory.Services.GetRequiredService<FakePushNotificationService>();

        await Scheduler.RunDueNowAsync(After(due), Ct);

        var run = (await RunsAsync(rule.Id)).Should().ContainSingle().Subject;
        run.Status.Should().Be(AutomationRunStatus.Sent);
        run.SentAt.Should().BeCloseTo(After(due), TimeSpan.FromSeconds(1));
        run.Occurrence.Should().StartWith("sched:");

        var notification = await WithDbAsync(db => db.Notifications.SingleAsync(
            n => n.RecipientUserId == client.UserId && n.Type == NotificationType.MeasurementRequested, Ct));
        run.DeliveredEntityId.Should().Be(notification.PublicId);
        push.Calls.Should().Contain(c => c.UserId == client.UserId);

        var next = (await ScheduleAsync(rule.Id, client.ProfileId))!.NextRunAt;
        next.Should().BeAfter(After(due));
    }

    [Fact]
    public async Task Run_AcrossThePragueSpringForward_RunsOncePerSideAtTheCorrectUtcInstant()
    {
        var (coach, client, _) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach);
        var changeDay = LastSundayOfMarch(DateTime.UtcNow.Year + 1);
        var saturdayDue = changeDay.AddDays(-1).ToDateTime(new TimeOnly(8, 0), DateTimeKind.Utc);
        var sundayDue = changeDay.ToDateTime(new TimeOnly(7, 0), DateTimeKind.Utc);
        var mondayDue = sundayDue.AddDays(1);

        // 09:00 Prague is 08:00Z before the change (CET, UTC+1) and 07:00Z after it (CEST, UTC+2).
        await Scheduler.RunDueNowAsync(saturdayDue.AddHours(-20), Ct);
        (await ScheduleAsync(rule.Id, client.ProfileId))!.NextRunAt.Should().Be(saturdayDue);

        await Scheduler.RunDueNowAsync(saturdayDue.AddMinutes(-1), Ct);
        (await RunsAsync(rule.Id)).Should().BeEmpty("the instant before the Saturday send is not due yet");

        await Scheduler.RunDueNowAsync(saturdayDue.AddMinutes(1), Ct);
        (await RunsAsync(rule.Id)).Should().ContainSingle(r => r.Status == AutomationRunStatus.Sent);
        (await ScheduleAsync(rule.Id, client.ProfileId))!.NextRunAt.Should().Be(
            sundayDue, "the next day is 23 hours later: 09:00 local moved from UTC+1 to UTC+2");

        await Scheduler.RunDueNowAsync(sundayDue.AddMinutes(-1), Ct);
        (await RunsAsync(rule.Id)).Should().ContainSingle("the Sunday send is due at 07:00Z, not at the old 08:00Z offset");

        await Scheduler.RunDueNowAsync(sundayDue.AddMinutes(1), Ct);
        await Scheduler.RunDueNowAsync(sundayDue.AddMinutes(2), Ct);

        var runs = await RunsAsync(rule.Id);
        runs.Should().HaveCount(2, "each side of the change sends exactly once");
        runs.Should().OnlyContain(r => r.Status == AutomationRunStatus.Sent);
        runs[0].Occurrence.Should().Be($"sched:{saturdayDue:yyyyMMddTHHmmss}");
        runs[1].Occurrence.Should().Be($"sched:{sundayDue:yyyyMMddTHHmmss}");
        (await ScheduleAsync(rule.Id, client.ProfileId))!.NextRunAt.Should().Be(mondayDue);
        (await NotificationCountAsync(client.UserId, NotificationType.MeasurementRequested)).Should().Be(2);
    }

    private static DateOnly LastSundayOfMarch(int year)
    {
        var lastDay = new DateOnly(year, 3, 31);

        return lastDay.AddDays(-(int)lastDay.DayOfWeek);
    }

    [Fact]
    public async Task Run_MeasurementRuleWithMessage_SendsItInTheNotification()
    {
        var (coach, client, _) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach, r => r.Message = "Morning, before breakfast");
        var due = await ArmAsync(rule, client);

        await Scheduler.RunDueNowAsync(After(due), Ct);

        var notification = await WithDbAsync(db => db.Notifications.SingleAsync(
            n => n.RecipientUserId == client.UserId && n.Type == NotificationType.MeasurementRequested, Ct));
        notification.Body.Should().Contain("Morning, before breakfast");
    }

    [Fact]
    public async Task Run_DueFormRule_CreatesAPendingResponseAndLeavesTheLinksFormAlone()
    {
        var (coach, client, linkId) = await PairAsync();
        var questionnaire = await AutomationTestData.InsertQuestionnaireAsync(factory, coach);
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach, r =>
        {
            r.ActionType = AutomationActionType.SendForm;
            r.QuestionnaireId = questionnaire.Id;
        });
        var due = await ArmAsync(rule, client);

        await Scheduler.RunDueNowAsync(After(due), Ct);

        var response = await WithDbAsync(db => db.QuestionnaireResponses.AsNoTracking()
            .SingleAsync(r => r.QuestionnaireId == questionnaire.Id && r.ClientId == client.UserId, Ct));
        response.Status.Should().Be(QuestionnaireResponseStatus.Pending);
        response.LinkId.Should().Be(linkId);
        response.ProfessionalId.Should().Be(coach.UserId);
        response.Kind.Should().Be(QuestionnaireResponseKind.Questionnaire);

        var run = (await RunsAsync(rule.Id)).Should().ContainSingle().Subject;
        run.Status.Should().Be(AutomationRunStatus.Sent);
        run.DeliveredEntityId.Should().Be(response.PublicId);

        var link = await WithDbAsync(db => db.ClientProfessionalLinks.AsNoTracking().SingleAsync(l => l.Id == linkId, Ct));
        link.QuestionnaireId.Should().BeNull("a delivered form never replaces the link's own questionnaire");
        (await NotificationCountAsync(client.UserId, NotificationType.QuestionnaireAssigned)).Should().Be(1);
    }

    [Theory]
    [InlineData(QuestionnaireType.CheckIn, true, false)]
    [InlineData(QuestionnaireType.Onboarding, false, false)]
    [InlineData(QuestionnaireType.Onboarding, true, true)]
    public async Task Run_FormThatIsNotUsable_RecordsFailedAndSendsNothing(
        QuestionnaireType type, bool isActive, bool ownedByAnotherCoach)
    {
        var (coach, client, _) = await PairAsync();
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "studio");
        var other = await TestActors.Trainer(factory).CreateAsync(Ct);
        var questionnaire = await AutomationTestData.InsertQuestionnaireAsync(
            factory, coach, type, isActive, ownedByAnotherCoach ? other.UserId : null);
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach, r =>
        {
            r.ActionType = AutomationActionType.SendForm;
            r.QuestionnaireId = questionnaire.Id;
        });
        var due = await ArmAsync(rule, client);

        await Scheduler.RunDueNowAsync(After(due), Ct);

        var run = (await RunsAsync(rule.Id)).Should().ContainSingle().Subject;
        run.Status.Should().Be(AutomationRunStatus.Failed);
        (await WithDbAsync(db => db.QuestionnaireResponses.CountAsync(r => r.ClientId == client.UserId, Ct))).Should().Be(0);
        (await ScheduleAsync(rule.Id, client.ProfileId))!.NextRunAt.Should().BeAfter(After(due), "a failed row is moved on, not retried every tick");
    }

    [Fact]
    public async Task Run_DuePhotoRule_SendsAOneOffBulkRequestOpenForSevenDays()
    {
        var (coach, client, linkId) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach, r =>
        {
            r.ActionType = AutomationActionType.RequestProgressPhotos;
            r.Message = "Front and side";
        });
        var due = await ArmAsync(rule, client);

        await Scheduler.RunDueNowAsync(After(due), Ct);

        var request = await WithDbAsync(db => db.PhotoDiaryRequests.AsNoTracking().SingleAsync(r => r.LinkId == linkId, Ct));
        request.Mode.Should().Be(PhotoDiaryMode.Bulk);
        request.DurationDays.Should().Be(7);
        request.Status.Should().Be(PhotoDiaryStatus.Pending);
        request.Message.Should().Be("Front and side");
        request.ProfessionalId.Should().Be(coach.UserId);

        var run = (await RunsAsync(rule.Id)).Should().ContainSingle().Subject;
        run.DeliveredEntityId.Should().Be(request.Id);
    }

    [Fact]
    public async Task Run_PhotoRuleWhileARequestIsOpen_SkipsAsPreviousUnanswered()
    {
        var (coach, client, linkId) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach, r =>
        {
            r.ActionType = AutomationActionType.RequestProgressPhotos;
            r.SkipWhilePreviousUnanswered = false;
        });
        var due = await ArmAsync(rule, client);

        await WithDbAsync(async db =>
        {
            db.PhotoDiaryRequests.Add(new PhotoDiaryRequest
            {
                Id = Guid.NewGuid(),
                ProfessionalId = coach.UserId,
                LinkId = linkId,
                Mode = PhotoDiaryMode.Bulk,
                DurationDays = 30,
                Status = PhotoDiaryStatus.Pending,
                CreatedAt = After(due),
                UpdatedAt = After(due),
            });
            await db.SaveChangesAsync(Ct);
            return 0;
        });

        await Scheduler.RunDueNowAsync(After(due).AddMinutes(1), Ct);

        var run = (await RunsAsync(rule.Id)).Should().ContainSingle().Subject;
        run.Status.Should().Be(AutomationRunStatus.Skipped);
        run.SkipReason.Should().Be(AutomationSkipReason.PreviousUnanswered);
        (await WithDbAsync(db => db.PhotoDiaryRequests.CountAsync(r => r.LinkId == linkId, Ct))).Should().Be(1);
    }

    [Fact]
    public async Task Run_PhotoRequestThatThrowsOnSave_RecordsFailedAndMovesTheRowOn()
    {
        var (coach, client, linkId) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach, r =>
        {
            r.ActionType = AutomationActionType.RequestProgressPhotos;
            r.Message = new string('x', 600);
        });
        var due = await ArmAsync(rule, client);

        var act = () => Scheduler.RunDueNowAsync(After(due), Ct);

        await act.Should().NotThrowAsync("a failing delivery never escapes the worker");
        var run = (await RunsAsync(rule.Id)).Should().ContainSingle().Subject;
        run.Status.Should().Be(AutomationRunStatus.Failed);
        (await WithDbAsync(db => db.PhotoDiaryRequests.CountAsync(r => r.LinkId == linkId, Ct))).Should().Be(0);
        (await ScheduleAsync(rule.Id, client.ProfileId))!.NextRunAt.Should().BeAfter(After(due));
    }

    // ── concurrency ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_TwoWorkersAtOnce_SendExactlyOnce()
    {
        var (coach, client, _) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach);
        var due = await ArmAsync(rule, client);

        await Task.WhenAll(
            Scheduler.RunDueNowAsync(After(due), Ct),
            Scheduler.RunDueNowAsync(After(due), Ct));

        (await RunsAsync(rule.Id)).Should().ContainSingle();
        (await NotificationCountAsync(client.UserId, NotificationType.MeasurementRequested)).Should().Be(1);
    }

    [Fact]
    public async Task Run_RowHeldByAnotherWorker_IsSkippedAndRunsOnceReleased()
    {
        var (coach, client, _) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach);
        var due = await ArmAsync(rule, client);
        var scheduleId = (await ScheduleAsync(rule.Id, client.ProfileId))!.Id;

        using (var holder = factory.Services.CreateScope())
        {
            var holderDb = holder.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await using var transaction = await holderDb.BeginTransactionAsync(Ct);
            await holderDb.Database.ExecuteSqlRawAsync(
                "SELECT id FROM automation_schedules WHERE id = {0} FOR UPDATE", [scheduleId], Ct);

            await Scheduler.RunDueNowAsync(After(due), Ct);

            (await RunsAsync(rule.Id)).Should().BeEmpty("a row another worker holds is passed over, not waited for");
        }

        await Scheduler.RunDueNowAsync(After(due), Ct);

        (await RunsAsync(rule.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Claim_RowHeldByAnotherClaim_IsSkippedByTheNext()
    {
        var (coach, client, _) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach);
        await ArmAsync(rule, client);

        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        var firstDb = first.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var secondDb = second.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var firstTransaction = await firstDb.BeginTransactionAsync(Ct);
        await using var secondTransaction = await secondDb.BeginTransactionAsync(Ct);

        var claimedFirst = await firstDb.ClaimDueAutomationScheduleAsync(DateTime.UtcNow.AddYears(1), Ct);
        var claimedSecond = await secondDb.ClaimDueAutomationScheduleAsync(DateTime.UtcNow.AddYears(1), Ct);

        claimedFirst.Should().NotBeNull();
        (claimedSecond is null || claimedSecond.Id != claimedFirst!.Id).Should().BeTrue("a row held by one claim is skipped by the other");
    }

    // ── options ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Options_SkipWhilePreviousUnanswered_SkipsUntilTheClientLogsAMeasurement()
    {
        var (coach, client, _) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach, r => r.SkipWhilePreviousUnanswered = true);
        var firstDue = await ArmAsync(rule, client);

        await Scheduler.RunDueNowAsync(After(firstDue), Ct);
        var secondDue = (await ScheduleAsync(rule.Id, client.ProfileId))!.NextRunAt!.Value;
        await Scheduler.RunDueNowAsync(After(secondDue), Ct);

        var afterSecond = await RunsAsync(rule.Id);
        afterSecond.Should().HaveCount(2);
        afterSecond[0].Status.Should().Be(AutomationRunStatus.Sent);
        afterSecond[1].Status.Should().Be(AutomationRunStatus.Skipped);
        afterSecond[1].SkipReason.Should().Be(AutomationSkipReason.PreviousUnanswered);

        var loggedAt = After(secondDue).AddMinutes(1);
        await AutomationTestData.AddMeasurementAsync(factory, client.ProfileId, loggedAt);
        var thirdDue = (await ScheduleAsync(rule.Id, client.ProfileId))!.NextRunAt!.Value;
        await Scheduler.RunDueNowAsync(After(thirdDue), Ct);

        var afterThird = await RunsAsync(rule.Id);
        afterThird.Should().HaveCount(3);
        afterThird[2].Status.Should().Be(AutomationRunStatus.Sent);
        afterThird[0].AnsweredAt.Should().BeCloseTo(loggedAt, TimeSpan.FromSeconds(1), "a measurement request is answered by the next logged measurement");
    }

    [Fact]
    public async Task Options_StopAfterSends_SendsThatManyAndNeverRearms()
    {
        var (coach, client, _) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach, r => r.StopAfterSends = 1);
        var due = await ArmAsync(rule, client);

        await Scheduler.RunDueNowAsync(After(due), Ct);
        await Scheduler.RunDueNowAsync(After(due).AddDays(3), Ct);
        await Scheduler.RunDueNowAsync(After(due).AddDays(6), Ct);

        (await RunsAsync(rule.Id)).Should().ContainSingle(r => r.Status == AutomationRunStatus.Sent);
        (await ScheduleAsync(rule.Id, client.ProfileId))?.NextRunAt.Should().BeNull();
    }

    [Fact]
    public async Task Options_OncePerPlan_SkipsASecondSendForTheSamePlanOnAnotherDate()
    {
        var (coach, client, _) = await PairAsync();
        var firstStart = DateTime.UtcNow.Date.AddDays(5);
        var planId = await AutomationTestData.SeedTrainingPlanAsync(factory, client.UserId, coach.UserId, firstStart, 4);
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach, r =>
        {
            r.TriggerKind = AutomationTriggerKind.Event;
            r.EventType = AutomationEventType.PlanStarted;
            r.EventOffsetDays = 0;
            r.OncePerPlan = true;
        });
        var firstDue = await ArmAsync(rule, client);

        await Scheduler.RunDueNowAsync(After(firstDue), Ct);

        var mongo = factory.Services.GetRequiredService<IMongoContext>();
        await mongo.TrainingPlans.UpdateOneAsync(
            Builders<TrainingPlan>.Filter.Eq(p => p.ExternalId, planId),
            Builders<TrainingPlan>.Update.Set(p => p.StartDate, firstStart.AddDays(2)),
            cancellationToken: Ct);
        await Scheduler.RunDueNowAsync(After(firstDue), Ct);
        var secondDue = (await ScheduleAsync(rule.Id, client.ProfileId))!.NextRunAt!.Value;
        await Scheduler.RunDueNowAsync(After(secondDue), Ct);

        var runs = await RunsAsync(rule.Id);
        runs.Should().HaveCount(2);
        runs[0].Status.Should().Be(AutomationRunStatus.Sent);
        runs[0].Occurrence.Should().StartWith($"PlanStarted:{planId:N}:");
        runs[1].Status.Should().Be(AutomationRunStatus.Skipped);
        runs[1].SkipReason.Should().Be(AutomationSkipReason.AlreadySentForPlan);
    }

    [Fact]
    public async Task Options_PlanStartedWithOffset_FiresOnceAtTheClientsLocalTime()
    {
        var (coach, client, _) = await PairAsync();
        var start = DateTime.UtcNow.Date.AddDays(8);
        await AutomationTestData.SeedTrainingPlanAsync(factory, client.UserId, coach.UserId, start, 4);
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach, r =>
        {
            r.TriggerKind = AutomationTriggerKind.Event;
            r.EventType = AutomationEventType.PlanStarted;
            r.EventOffsetDays = 2;
            r.EventOffsetDirection = AutomationOffsetDirection.Before;
            r.LocalTime = new TimeOnly(18, 30);
        });
        var due = await ArmAsync(rule, client);

        due.Should().Be(AutomationScheduleCalculator.ToUtc(
            DateOnly.FromDateTime(start).AddDays(-2), new TimeOnly(18, 30), Prague));

        await Scheduler.RunDueNowAsync(After(due), Ct);
        await Scheduler.RunDueNowAsync(After(due).AddMinutes(5), Ct);

        (await RunsAsync(rule.Id)).Should().ContainSingle(r => r.Status == AutomationRunStatus.Sent);
    }

    [Fact]
    public async Task Options_PauseLandsAfterTheRowWasArmed_RecordsSkippedPaused()
    {
        var (coach, client, _) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach);
        var due = await ArmAsync(rule, client);
        await PauseAsync(rule, client);

        await Scheduler.RunDueNowAsync(After(due), Ct);

        var run = (await RunsAsync(rule.Id)).Should().ContainSingle().Subject;
        run.Status.Should().Be(AutomationRunStatus.Skipped);
        run.SkipReason.Should().Be(AutomationSkipReason.Paused);
        (await ScheduleAsync(rule.Id, client.ProfileId))!.NextRunAt.Should().BeNull();
        (await NotificationCountAsync(client.UserId, NotificationType.MeasurementRequested)).Should().Be(0);
    }

    [Fact]
    public async Task Run_StoredRunNoLongerMatchesTheRule_ReadvancesWithoutSending()
    {
        var (coach, client, _) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach);
        var due = await ArmAsync(rule, client);
        await WithDbAsync(async db =>
        {
            await db.AutomationRules.Where(r => r.Id == rule.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ScheduleInterval, 3), Ct);
            return 0;
        });

        await Scheduler.RunDueNowAsync(After(due), Ct);

        (await RunsAsync(rule.Id)).Should().BeEmpty("the stored instant is not an occurrence of the edited rule");
        (await ScheduleAsync(rule.Id, client.ProfileId))!.NextRunAt.Should().BeAfter(After(due));
    }

    // ── re-checks before sending ────────────────────────────────────────────

    [Fact]
    public async Task Run_CoachRoleRemovedAfterArming_SendsNothing()
    {
        var (coach, client, _) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach);
        var due = await ArmAsync(rule, client);
        await WithDbAsync(async db =>
        {
            await db.ProfessionalProfiles.Where(p => p.Id == coach.ProfileId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.TrainerRoleRemovedAt, DateTime.UtcNow), Ct);
            return 0;
        });

        await Scheduler.RunDueNowAsync(After(due), Ct);

        (await RunsAsync(rule.Id)).Should().BeEmpty();
        (await NotificationCountAsync(client.UserId, NotificationType.MeasurementRequested)).Should().Be(0);
        (await ScheduleAsync(rule.Id, client.ProfileId))?.NextRunAt.Should().BeNull();
    }

    [Fact]
    public async Task Reconcile_CoachRoleAlreadyRemoved_ArmsNothing()
    {
        var (coach, client, _) = await PairAsync(coach: c => c.WithRemovedCoachRoles(UserRole.Trainer));
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach);

        await Scheduler.RunDueNowAsync(DateTime.UtcNow, Ct);
        await Scheduler.RunDueNowAsync(DateTime.UtcNow.AddDays(3), Ct);

        (await ScheduleAsync(rule.Id, client.ProfileId)).Should().BeNull();
        (await RunsAsync(rule.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Run_LinkGrantsNothingAfterArming_SendsNothing()
    {
        var (coach, client, linkId) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach);
        var due = await ArmAsync(rule, client);
        await WithDbAsync(async db =>
        {
            await db.ClientProfessionalLinks.Where(l => l.Id == linkId).ExecuteUpdateAsync(
                s => s.SetProperty(l => l.CanViewNutritionPlans, false).SetProperty(l => l.CanViewTrainingPlans, false), Ct);
            return 0;
        });

        await Scheduler.RunDueNowAsync(After(due), Ct);

        (await RunsAsync(rule.Id)).Should().BeEmpty();
        (await ScheduleAsync(rule.Id, client.ProfileId))?.NextRunAt.Should().BeNull();
    }

    [Fact]
    public async Task Reconcile_LinkGrantsNothing_ArmsNothing()
    {
        var (coach, client, _) = await PairAsync(link: l => l.CanViewNutritionPlans(false).CanViewTrainingPlans(false));
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach);

        await Scheduler.RunDueNowAsync(DateTime.UtcNow, Ct);

        (await ScheduleAsync(rule.Id, client.ProfileId)).Should().BeNull();
    }

    [Fact]
    public async Task Reconcile_LinkEnded_RemovesTheRow()
    {
        var (coach, client, linkId) = await PairAsync();
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach);
        await ArmAsync(rule, client);
        await WithDbAsync(async db =>
        {
            await db.ClientProfessionalLinks.Where(l => l.Id == linkId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ConnectionState, ConnectionState.Ended), Ct);
            return 0;
        });

        await Scheduler.RunDueNowAsync(DateTime.UtcNow, Ct);

        (await ScheduleAsync(rule.Id, client.ProfileId)).Should().BeNull();
    }

    // ── polled events ───────────────────────────────────────────────────────

    private static Task<AutomationRule> EventRuleAsync(
        FitnessApiFactory factory, Actor coach, AutomationEventType eventType, int offsetDays, Action<AutomationRule>? configure = null) =>
        AutomationTestData.InsertRuleAsync(factory, coach, r =>
        {
            r.TriggerKind = AutomationTriggerKind.Event;
            r.EventType = eventType;
            r.EventOffsetDays = offsetDays;
            r.EventOffsetDirection = AutomationOffsetDirection.After;
            r.LocalTime = new TimeOnly(9, 0);
            configure?.Invoke(r);
        });

    [Fact]
    public async Task Poll_ClientJoinedAfterTheRule_FiresAtTheLocalTimeAfterTheOffset()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var rule = await EventRuleAsync(factory, coach, AutomationEventType.ClientJoined, 1);
        var linkId = await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        var joinedAt = await AutomationTestData.GetLinkCreatedAsync(factory, linkId);
        var fireAt = AutomationPolledEventCalculator.ComputeFireAt(joinedAt, 1, new TimeOnly(9, 0), Prague);

        await Scheduler.RunDueNowAsync(fireAt.AddMinutes(-1), Ct);
        (await RunsAsync(rule.Id)).Should().BeEmpty("the send is not due yet");

        await Scheduler.RunDueNowAsync(After(fireAt), Ct);
        await Scheduler.RunDueNowAsync(After(fireAt).AddMinutes(2), Ct);

        var run = (await RunsAsync(rule.Id)).Should().ContainSingle().Subject;
        run.Status.Should().Be(AutomationRunStatus.Sent);
        run.Occurrence.Should().Be($"joined:{linkId}");
        (await NotificationCountAsync(client.UserId, NotificationType.MeasurementRequested)).Should().Be(1);
    }

    [Fact]
    public async Task Poll_ClientJoinedBeforeTheRuleExisted_NeverFires()
    {
        var (coach, client, linkId) = await PairAsync();
        var rule = await EventRuleAsync(factory, coach, AutomationEventType.ClientJoined, 0);
        var joinedAt = await AutomationTestData.GetLinkCreatedAsync(factory, linkId);
        var fireAt = AutomationPolledEventCalculator.ComputeFireAt(joinedAt, 0, new TimeOnly(9, 0), Prague);

        await Scheduler.RunDueNowAsync(After(fireAt), Ct);

        (await RunsAsync(rule.Id)).Should().BeEmpty();
        (await NotificationCountAsync(client.UserId, NotificationType.MeasurementRequested)).Should().Be(0);
    }

    [Fact]
    public async Task Poll_ClientJoinedLongBeforeTheLookback_DoesNotFireLate()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var rule = await EventRuleAsync(factory, coach, AutomationEventType.ClientJoined, 1);
        var linkId = await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        var joinedAt = await AutomationTestData.GetLinkCreatedAsync(factory, linkId);
        var fireAt = AutomationPolledEventCalculator.ComputeFireAt(joinedAt, 1, new TimeOnly(9, 0), Prague);

        await Scheduler.RunDueNowAsync(fireAt + AutomationPolledEventCalculator.Lookback + TimeSpan.FromHours(1), Ct);

        (await RunsAsync(rule.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Poll_MissedCheckIn_FiresOnlyForAPastDueUnansweredOne()
    {
        var (coach, client, linkId) = await PairAsync();
        var rule = await EventRuleAsync(factory, coach, AutomationEventType.CheckInMissed, 0);
        var form = await AutomationTestData.InsertQuestionnaireAsync(factory, coach, QuestionnaireType.CheckIn);
        var dueAt = DateTime.UtcNow;

        await WithDbAsync(async db =>
        {
            QuestionnaireResponse CheckIn(DateOnly week, QuestionnaireResponseStatus status) => new()
            {
                QuestionnaireId = form.Id,
                ClientId = client.UserId,
                ProfessionalId = coach.UserId,
                LinkId = linkId,
                Kind = QuestionnaireResponseKind.CheckIn,
                Status = status,
                CheckInWeekStart = week,
                CheckInDueAtUtc = dueAt,
                SubmittedAt = status == QuestionnaireResponseStatus.Submitted ? dueAt.AddHours(-1) : null,
            };

            var weekStart = DateOnly.FromDateTime(DateTime.UtcNow);
            db.QuestionnaireResponses.Add(CheckIn(weekStart, QuestionnaireResponseStatus.Submitted));
            db.QuestionnaireResponses.Add(CheckIn(weekStart.AddDays(-7), QuestionnaireResponseStatus.Pending));
            await db.SaveChangesAsync(Ct);
            return 0;
        });
        var fireAt = AutomationPolledEventCalculator.ComputeFireAt(dueAt, 0, new TimeOnly(9, 0), Prague);

        await Scheduler.RunDueNowAsync(After(fireAt), Ct);

        var run = (await RunsAsync(rule.Id)).Should().ContainSingle().Subject;
        run.Occurrence.Should().StartWith("checkin:");
        run.Status.Should().Be(AutomationRunStatus.Sent);
    }


    [Fact]
    public async Task Poll_ClientInactiveForTheDays_FiresOnceForThatInactivity()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var linkId = await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        var linkedAt = DateTime.UtcNow.AddDays(-4);
        await AutomationTestData.SetLinkCreatedAsync(factory, linkId, linkedAt);
        var rule = await EventRuleAsync(factory, coach, AutomationEventType.ClientInactive, 3);
        await AutomationTestData.SetRuleCreatedAsync(factory, rule.Id, DateTime.UtcNow.AddDays(-10));
        var fireAt = AutomationPolledEventCalculator.ComputeFireAt(linkedAt.AddDays(3), 0, new TimeOnly(9, 0), Prague);

        await Scheduler.RunDueNowAsync(After(fireAt), Ct);
        await Scheduler.RunDueNowAsync(After(fireAt).AddMinutes(3), Ct);

        var run = (await RunsAsync(rule.Id)).Should().ContainSingle().Subject;
        run.Occurrence.Should().StartWith("inactive:");
        run.Status.Should().Be(AutomationRunStatus.Sent);
    }

    [Fact]
    public async Task Poll_ClientInactiveForTheDays_NeverSendsBeforeTheDaysHaveElapsed()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var linkId = await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        var linkedAt = DateTime.UtcNow.Date.AddDays(-4).AddHours(20);
        await AutomationTestData.SetLinkCreatedAsync(factory, linkId, linkedAt);
        var rule = await EventRuleAsync(factory, coach, AutomationEventType.ClientInactive, 3);
        await AutomationTestData.SetRuleCreatedAsync(factory, rule.Id, DateTime.UtcNow.AddDays(-10));
        var inactiveSince = linkedAt.AddDays(3);

        await Scheduler.RunDueNowAsync(inactiveSince.AddMinutes(30), Ct);
        (await RunsAsync(rule.Id)).Should().BeEmpty("the 09:00 on the day the days elapse is already behind; the next send time is the following morning");

        var fireAt = AutomationPolledEventCalculator.ComputeFireAt(inactiveSince, 0, new TimeOnly(9, 0), Prague);
        await Scheduler.RunDueNowAsync(After(fireAt), Ct);
        (await RunsAsync(rule.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Poll_ClientActiveRecently_DoesNotFireInactive()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var linkId = await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        var linkedAt = DateTime.UtcNow.AddDays(-4);
        await AutomationTestData.SetLinkCreatedAsync(factory, linkId, linkedAt);
        var rule = await EventRuleAsync(factory, coach, AutomationEventType.ClientInactive, 3);
        await AutomationTestData.SetRuleCreatedAsync(factory, rule.Id, DateTime.UtcNow.AddDays(-10));
        await AutomationTestData.AddMeasurementAsync(factory, client.ProfileId, DateTime.UtcNow.AddHours(-2));
        var fireAt = AutomationPolledEventCalculator.ComputeFireAt(linkedAt.AddDays(3), 0, new TimeOnly(9, 0), Prague);

        await Scheduler.RunDueNowAsync(After(fireAt), Ct);

        (await RunsAsync(rule.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Poll_SeveralClientsAndTwoInactiveRules_FiresOnlyForTheClientWithNoActivityInEachRule()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var linkedAt = DateTime.UtcNow.AddDays(-5);
        var idle = await TestActors.Client(factory).CreateAsync(Ct);
        var measured = await TestActors.Client(factory).CreateAsync(Ct);
        var trained = await TestActors.Client(factory).CreateAsync(Ct);
        var fed = await TestActors.Client(factory).CreateAsync(Ct);

        foreach (var client in new[] { idle, measured, trained, fed })
        {
            var linkId = await TestActors.Link(factory, coach, client).CreateAsync(Ct);
            await AutomationTestData.SetLinkCreatedAsync(factory, linkId, linkedAt);
        }

        var shortRule = await EventRuleAsync(factory, coach, AutomationEventType.ClientInactive, 3);
        var longRule = await EventRuleAsync(factory, coach, AutomationEventType.ClientInactive, 4);
        await AutomationTestData.SetRuleCreatedAsync(factory, shortRule.Id, DateTime.UtcNow.AddDays(-10));
        await AutomationTestData.SetRuleCreatedAsync(factory, longRule.Id, DateTime.UtcNow.AddDays(-10));

        await AutomationTestData.AddMeasurementAsync(factory, measured.ProfileId, DateTime.UtcNow.AddHours(-2));

        var mongo = factory.Services.GetRequiredService<IMongoContext>();
        var workout = WorkoutLogTestHelpers.CreateLog(clientId: trained.UserId, isCompleted: true, startedAt: DateTime.UtcNow.AddHours(-2));
        await mongo.SessionExecutions.InsertOneAsync(workout, cancellationToken: Ct);
        await mongo.MealLogs.InsertOneAsync(
            new MealLog { ClientId = fed.UserId, LogDate = DateTime.UtcNow.Date, EatenAt = DateTime.UtcNow.AddHours(-2) },
            cancellationToken: Ct);

        var lastFireAt = AutomationPolledEventCalculator.ComputeFireAt(linkedAt.AddDays(4), 0, new TimeOnly(9, 0), Prague);
        await Scheduler.RunDueNowAsync(After(lastFireAt), Ct);
        await Scheduler.RunDueNowAsync(After(lastFireAt).AddMinutes(3), Ct);

        foreach (var rule in new[] { shortRule, longRule })
        {
            var run = (await RunsAsync(rule.Id)).Should().ContainSingle("only the idle client has been inactive").Subject;
            run.ClientProfileId.Should().Be(idle.ProfileId);
            run.Status.Should().Be(AutomationRunStatus.Sent);
        }
    }

    [Fact]
    public async Task Poll_FormAnswered_IgnoresCheckInFormsAndItsOwnDeliveredResponse()
    {
        var (coach, client, linkId) = await PairAsync();
        var delivered = await AutomationTestData.InsertQuestionnaireAsync(factory, coach);
        var rule = await EventRuleAsync(factory, coach, AutomationEventType.FormAnswered, 0, r =>
        {
            r.ActionType = AutomationActionType.SendForm;
            r.QuestionnaireId = delivered.Id;
        });
        var checkInForm = await AutomationTestData.InsertQuestionnaireAsync(factory, coach, QuestionnaireType.CheckIn);
        var answeredForm = await AutomationTestData.InsertQuestionnaireAsync(factory, coach);
        var submittedAt = DateTime.UtcNow;

        await AutomationTestData.AddResponseAsync(
            factory, checkInForm, coach, client, linkId, QuestionnaireResponseStatus.Submitted, submittedAt);
        var fireAt = AutomationPolledEventCalculator.ComputeFireAt(submittedAt, 0, new TimeOnly(9, 0), Prague);

        await Scheduler.RunDueNowAsync(After(fireAt), Ct);
        (await RunsAsync(rule.Id)).Should().BeEmpty("a check-in form is not a form the client answered");

        await AutomationTestData.AddResponseAsync(
            factory, answeredForm, coach, client, linkId, QuestionnaireResponseStatus.Submitted, submittedAt);
        await Scheduler.RunDueNowAsync(After(fireAt).AddMinutes(1), Ct);

        var run = (await RunsAsync(rule.Id)).Should().ContainSingle().Subject;
        run.Status.Should().Be(AutomationRunStatus.Sent);

        // The client now answers the very form the rule delivered: that must not trigger the rule again.
        var deliveredResponse = await WithDbAsync(db => db.QuestionnaireResponses.SingleAsync(
            r => r.PublicId == run.DeliveredEntityId, Ct));
        await WithDbAsync(async db =>
        {
            await db.QuestionnaireResponses.Where(r => r.Id == deliveredResponse.Id).ExecuteUpdateAsync(
                s => s.SetProperty(r => r.Status, QuestionnaireResponseStatus.Submitted)
                    .SetProperty(r => r.SubmittedAt, submittedAt.AddMinutes(5)), Ct);
            return 0;
        });
        await Scheduler.RunDueNowAsync(After(fireAt).AddMinutes(10), Ct);

        (await RunsAsync(rule.Id)).Should().ContainSingle("the response the rule delivered never counts as an answered form");
    }

    // ── answers ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_SubmittedDeliveredForm_StampsAnsweredAt()
    {
        var (coach, client, _) = await PairAsync();
        var questionnaire = await AutomationTestData.InsertQuestionnaireAsync(factory, coach);
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach, r =>
        {
            r.ActionType = AutomationActionType.SendForm;
            r.QuestionnaireId = questionnaire.Id;
        });
        var due = await ArmAsync(rule, client);
        await Scheduler.RunDueNowAsync(After(due), Ct);
        var run = (await RunsAsync(rule.Id)).Single();
        var submittedAt = After(due).AddHours(2);

        await WithDbAsync(async db =>
        {
            await db.QuestionnaireResponses.Where(r => r.PublicId == run.DeliveredEntityId).ExecuteUpdateAsync(
                s => s.SetProperty(r => r.Status, QuestionnaireResponseStatus.Submitted)
                    .SetProperty(r => r.SubmittedAt, submittedAt), Ct);
            return 0;
        });
        await Scheduler.RunDueNowAsync(After(due).AddHours(3), Ct);

        (await RunsAsync(rule.Id)).Single().AnsweredAt.Should().BeCloseTo(submittedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Sweep_SeveralOpenRunsOfEveryAction_StampsExactlyTheAnsweredOnes()
    {
        var (coach, clientA, linkA) = await PairAsync();
        var clientB = await TestActors.Client(factory).CreateAsync(Ct);
        var linkB = await TestActors.Link(factory, coach, clientB).CreateAsync(Ct);
        var sentAt = DateTime.UtcNow.AddHours(-2);
        var answeredAt = DateTime.UtcNow.AddHours(-1);

        var formRule = await AutomationTestData.InsertRuleAsync(factory, coach, r => r.ActionType = AutomationActionType.SendForm);
        var photoRule = await AutomationTestData.InsertRuleAsync(factory, coach, r => r.ActionType = AutomationActionType.RequestProgressPhotos);
        var measurementRule = await AutomationTestData.InsertRuleAsync(factory, coach);
        var questionnaire = await AutomationTestData.InsertQuestionnaireAsync(factory, coach);

        var submittedForm = await AutomationTestData.AddResponseAsync(
            factory, questionnaire, coach, clientA, linkA, QuestionnaireResponseStatus.Submitted, answeredAt);
        var pendingForm = await AutomationTestData.AddResponseAsync(
            factory, questionnaire, coach, clientB, linkB, QuestionnaireResponseStatus.Pending, null);

        var completedPhotos = Guid.NewGuid();
        var openPhotos = Guid.NewGuid();
        await WithDbAsync(async db =>
        {
            db.PhotoDiaryRequests.AddRange(
                new PhotoDiaryRequest
                {
                    Id = completedPhotos,
                    ProfessionalId = coach.UserId,
                    LinkId = linkA,
                    Mode = PhotoDiaryMode.Bulk,
                    DurationDays = 7,
                    Status = PhotoDiaryStatus.Completed,
                    AcceptedAt = new DateTimeOffset(DateTime.SpecifyKind(sentAt, DateTimeKind.Utc)),
                    CompletedAt = new DateTimeOffset(DateTime.SpecifyKind(answeredAt, DateTimeKind.Utc)),
                    CreatedAt = sentAt,
                    UpdatedAt = answeredAt,
                },
                new PhotoDiaryRequest
                {
                    Id = openPhotos,
                    ProfessionalId = coach.UserId,
                    LinkId = linkB,
                    Mode = PhotoDiaryMode.Bulk,
                    DurationDays = 7,
                    Status = PhotoDiaryStatus.Pending,
                    CreatedAt = sentAt,
                    UpdatedAt = sentAt,
                });
            await db.SaveChangesAsync(Ct);
            return 0;
        });

        await AutomationTestData.AddMeasurementAsync(factory, clientA.ProfileId, answeredAt);
        await AutomationTestData.AddMeasurementAsync(factory, clientB.ProfileId, sentAt.AddHours(-1));

        void AddRun(ApplicationDbContext db, AutomationRule rule, Actor client, Guid? delivered)
        {
            var run = new AutomationRun
            {
                AutomationRuleId = rule.Id,
                ClientProfileId = client.ProfileId,
                Occurrence = Guid.NewGuid().ToString("N"),
                Status = AutomationRunStatus.Sent,
                SentAt = sentAt,
                DeliveredEntityId = delivered,
            };
            db.AutomationRuns.Add(run);
        }

        await WithDbAsync(async db =>
        {
            AddRun(db, formRule, clientA, submittedForm.PublicId);
            AddRun(db, formRule, clientB, pendingForm.PublicId);
            AddRun(db, photoRule, clientA, completedPhotos);
            AddRun(db, photoRule, clientB, openPhotos);
            AddRun(db, measurementRule, clientA, null);
            AddRun(db, measurementRule, clientB, null);
            await db.SaveChangesAsync(Ct);
            return 0;
        });

        await Scheduler.RunDueNowAsync(DateTime.UtcNow, Ct);

        async Task<Dictionary<long, DateTime?>> AnsweredByClientAsync(AutomationRule rule) =>
            (await RunsAsync(rule.Id)).ToDictionary(r => r.ClientProfileId, r => r.AnsweredAt);

        var forms = await AnsweredByClientAsync(formRule);
        forms[clientA.ProfileId].Should().BeCloseTo(answeredAt, TimeSpan.FromSeconds(1));
        forms[clientB.ProfileId].Should().BeNull();

        var photos = await AnsweredByClientAsync(photoRule);
        photos[clientA.ProfileId].Should().BeCloseTo(answeredAt, TimeSpan.FromSeconds(1));
        photos[clientB.ProfileId].Should().BeNull();

        var measurements = await AnsweredByClientAsync(measurementRule);
        measurements[clientA.ProfileId].Should().BeCloseTo(answeredAt, TimeSpan.FromSeconds(1));
        measurements[clientB.ProfileId].Should().BeNull("a measurement logged before the request was sent does not answer it");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private async Task PauseAsync(AutomationRule rule, Actor client, DateOnly? pausedUntil = null) =>
        await WithDbAsync(async db =>
        {
            db.AutomationClientOverrides.Add(new AutomationClientOverride
            {
                AutomationRuleId = rule.Id,
                ClientProfileId = client.ProfileId,
                IsPaused = true,
                PausedUntil = pausedUntil,
            });
            await db.SaveChangesAsync(Ct);
            return 0;
        });

    private static async Task<GetClientAutomationsResponse> ListAsync(Actor coach, Actor client)
    {
        var response = await coach.Http.GetAsync($"/trainer/clients/{client.PublicId}/automations", Ct);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<GetClientAutomationsResponse>(AutomationTestData.Json, Ct))!;
    }
}
