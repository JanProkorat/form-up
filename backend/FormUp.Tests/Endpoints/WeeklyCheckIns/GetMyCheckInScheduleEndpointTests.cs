using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.Automations;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.WeeklyCheckIns;

/// <summary>
/// Integration tests for <c>GET /client/weekly-check-ins/schedule</c>: the client's check-in days come from
/// the coaches' enabled check-in rules (or the client's own override) that still reach the client.
/// </summary>
[Collection(TestCollection.Shard2)]
public class GetMyCheckInScheduleEndpointTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record ScheduleResult(List<ScheduleItem> Items);

    private record ScheduleItem(
        Guid ProfessionalPublicId,
        string ProfessionalName,
        string Profession,
        string SendWeekday,
        string SendTime,
        string AnswerUntilWeekday);

    private async Task<Actor> SubscribedCoachAsync(string firstName = "Eva", string lastName = "Coach")
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist)
            .WithName(firstName, lastName)
            .CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "studio");

        return coach;
    }

    private async Task<AutomationRule> AddCheckInRuleAsync(
        Actor coach, DayOfWeek day, TimeOnly? time = null, int windowHours = 48, Action<AutomationRule>? configure = null)
    {
        var form = await AutomationTestData.InsertQuestionnaireAsync(factory, coach, QuestionnaireType.CheckIn);

        return await AutomationTestData.InsertRuleAsync(factory, coach, rule =>
        {
            rule.ActionType = AutomationActionType.SendForm;
            rule.QuestionnaireId = form.Id;
            rule.ScheduleUnit = AutomationScheduleUnit.Weeks;
            rule.ScheduleWeekday = day;
            rule.LocalTime = time ?? new TimeOnly(18, 0);
            rule.AnswerWindowHours = windowHours;
            configure?.Invoke(rule);
        });
    }

    private async Task AddOverrideAsync(
        AutomationRule rule, Actor client, DayOfWeek? day, TimeOnly time, int windowHours,
        bool enabled = true, bool isPaused = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.AutomationClientOverrides.Add(new AutomationClientOverride
        {
            AutomationRuleId = rule.Id,
            ClientProfileId = client.ProfileId,
            IsCustomized = true,
            TriggerKind = AutomationTriggerKind.Schedule,
            ScheduleInterval = 1,
            ScheduleUnit = AutomationScheduleUnit.Weeks,
            LocalTime = time,
            ScheduleWeekday = day,
            AnswerWindowHours = windowHours,
            Enabled = enabled,
            IsPaused = isPaused,
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<List<ScheduleItem>> GetScheduleAsync(Actor client)
    {
        var response = await client.Http.GetAsync("/client/weekly-check-ins/schedule", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ScheduleResult>(cancellationToken: Ct);

        return body!.Items;
    }

    [Fact]
    public async Task Get_RuleOnly_ReturnsRuleScheduleForTheLinkProfession()
    {
        var coach = await SubscribedCoachAsync("Eva", "Novak");
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).AsRole(UserRole.Nutritionist).CreateAsync(Ct);
        await AddCheckInRuleAsync(coach, DayOfWeek.Wednesday, new TimeOnly(18, 0), windowHours: 48);

        var items = await GetScheduleAsync(client);

        items.Should().ContainSingle().Which.Should().Be(new ScheduleItem(
            coach.PublicId, "Eva Novak", "Nutrition", "Wednesday", "18:00", "Friday"));
    }

    [Fact]
    public async Task Get_OverrideApplies_ReturnsOverrideWeekdayTimeAndWindow()
    {
        var coach = await SubscribedCoachAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        var rule = await AddCheckInRuleAsync(coach, DayOfWeek.Wednesday);
        await AddOverrideAsync(rule, client, DayOfWeek.Friday, new TimeOnly(7, 30), windowHours: 24);

        var item = (await GetScheduleAsync(client)).Should().ContainSingle().Subject;

        item.SendWeekday.Should().Be("Friday");
        item.SendTime.Should().Be("07:30");
        item.AnswerUntilWeekday.Should().Be("Saturday");
        item.Profession.Should().Be("Training");
    }

    [Fact]
    public async Task Get_OverrideWhilePerClientConfigIsOff_ReturnsRuleSchedule()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeAsync(factory, coach, canUseWeeklyCheckIns: true, canUsePerClientCheckInConfig: false);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        var rule = await AddCheckInRuleAsync(coach, DayOfWeek.Wednesday, windowHours: 48);
        await AddOverrideAsync(rule, client, DayOfWeek.Friday, new TimeOnly(7, 30), windowHours: 24);

        var item = (await GetScheduleAsync(client)).Should().ContainSingle().Subject;

        item.SendWeekday.Should().Be("Wednesday");
        item.SendTime.Should().Be("18:00");
        item.AnswerUntilWeekday.Should().Be("Friday");
    }

    [Fact]
    public async Task Get_NoCheckInRule_ReturnsEmptyList()
    {
        var coach = await SubscribedCoachAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);

        (await GetScheduleAsync(client)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Get_CoachMissingAnEntitlement_LeavesTheCoachOut(bool canUseAutomations, bool canUseWeeklyCheckIns)
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer).CreateAsync(Ct);
        await CoachSubscriptionBuilder.MoveToPlanAsync(factory, coach, plan =>
        {
            plan.CanUseAutomations = canUseAutomations;
            plan.CanUseWeeklyCheckIns = canUseWeeklyCheckIns;
        });
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        await AddCheckInRuleAsync(coach, DayOfWeek.Wednesday);

        (await GetScheduleAsync(client)).Should().BeEmpty();
    }

    [Fact]
    public async Task Get_RuleDisabledOrOverrideDisablesOrPauses_LeavesTheCoachOut()
    {
        var disabledCoach = await SubscribedCoachAsync();
        var disabledClient = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, disabledCoach, disabledClient).CreateAsync(Ct);
        await AddCheckInRuleAsync(disabledCoach, DayOfWeek.Wednesday, configure: rule => rule.Enabled = false);

        var overrideCoach = await SubscribedCoachAsync();
        var overrideClient = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, overrideCoach, overrideClient).CreateAsync(Ct);
        var overrideRule = await AddCheckInRuleAsync(overrideCoach, DayOfWeek.Wednesday);
        await AddOverrideAsync(overrideRule, overrideClient, null, new TimeOnly(9, 0), 48, enabled: false);

        var pausedCoach = await SubscribedCoachAsync();
        var pausedClient = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, pausedCoach, pausedClient).CreateAsync(Ct);
        var pausedRule = await AddCheckInRuleAsync(pausedCoach, DayOfWeek.Wednesday);
        await AddOverrideAsync(pausedRule, pausedClient, DayOfWeek.Wednesday, new TimeOnly(9, 0), 48, isPaused: true);

        (await GetScheduleAsync(disabledClient)).Should().BeEmpty();
        (await GetScheduleAsync(overrideClient)).Should().BeEmpty();
        (await GetScheduleAsync(pausedClient)).Should().BeEmpty();
    }

    [Fact]
    public async Task Get_ClientOutsideTheRuleAudience_LeavesTheCoachOut()
    {
        var coach = await SubscribedCoachAsync();
        var tagged = await TestActors.Client(factory).CreateAsync(Ct);
        var untagged = await TestActors.Client(factory).CreateAsync(Ct);
        var taggedLinkId = await TestActors.Link(factory, coach, tagged).CreateAsync(Ct);
        await TestActors.Link(factory, coach, untagged).CreateAsync(Ct);
        var (_, tagId) = await AutomationTestData.CreateTagAsync(factory, coach);
        await AutomationTestData.AssignTagAsync(factory, tagId, taggedLinkId);
        await AddCheckInRuleAsync(coach, DayOfWeek.Thursday, configure: rule =>
        {
            rule.Audience = AutomationAudience.Tag;
            rule.AudienceTagId = tagId;
        });

        var onlyForOther = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, onlyForOther).CreateAsync(Ct);
        await AddCheckInRuleAsync(coach, DayOfWeek.Saturday, configure: rule => rule.ClientProfileId = onlyForOther.ProfileId);

        (await GetScheduleAsync(tagged)).Should().ContainSingle().Which.SendWeekday.Should().Be("Thursday");
        (await GetScheduleAsync(untagged)).Should().BeEmpty();
        (await GetScheduleAsync(onlyForOther)).Should().ContainSingle().Which.SendWeekday.Should().Be("Saturday");
    }

    [Fact]
    public async Task Get_EndedLinkOrRemovedCoachRole_LeavesTheCoachOutAndShowsNoOtherClientsData()
    {
        var endedCoach = await SubscribedCoachAsync();
        var endedClient = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, endedCoach, endedClient).WithState(ConnectionState.Ended).CreateAsync(Ct);
        await AddCheckInRuleAsync(endedCoach, DayOfWeek.Wednesday);

        var removedCoach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist)
            .WithRemovedCoachRoles(UserRole.Trainer)
            .CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, removedCoach, "studio");
        var removedClient = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, removedCoach, removedClient).AsRole(UserRole.Trainer).CreateAsync(Ct);
        await AddCheckInRuleAsync(removedCoach, DayOfWeek.Wednesday);

        var liveCoach = await SubscribedCoachAsync();
        var liveClient = await TestActors.Client(factory).CreateAsync(Ct);
        var otherClient = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, liveCoach, liveClient).CreateAsync(Ct);
        await TestActors.Link(factory, liveCoach, otherClient).CreateAsync(Ct);
        var liveRule = await AddCheckInRuleAsync(liveCoach, DayOfWeek.Wednesday);
        await AddOverrideAsync(liveRule, otherClient, DayOfWeek.Sunday, new TimeOnly(6, 0), 12);

        (await GetScheduleAsync(endedClient)).Should().BeEmpty();
        (await GetScheduleAsync(removedClient)).Should().BeEmpty();
        (await GetScheduleAsync(liveClient)).Should().ContainSingle().Which.SendWeekday.Should().Be("Wednesday");
    }

    [Fact]
    public async Task Get_SeveralCoaches_ReturnsOneEntryPerCoach()
    {
        var first = await SubscribedCoachAsync("Anna", "First");
        var second = await SubscribedCoachAsync("Boris", "Second");
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, first, client).AsRole(UserRole.Trainer).CreateAsync(Ct);
        await TestActors.Link(factory, second, client).AsRole(UserRole.Nutritionist).CreateAsync(Ct);
        await AddCheckInRuleAsync(first, DayOfWeek.Thursday, new TimeOnly(18, 0), windowHours: 48);
        await AddCheckInRuleAsync(second, DayOfWeek.Monday, new TimeOnly(8, 15), windowHours: 72);

        var items = await GetScheduleAsync(client);

        items.Should().HaveCount(2);
        items.Single(i => i.ProfessionalPublicId == first.PublicId).Should().Match<ScheduleItem>(i =>
            i.Profession == "Training" && i.SendWeekday == "Thursday" && i.SendTime == "18:00" && i.AnswerUntilWeekday == "Saturday");
        items.Single(i => i.ProfessionalPublicId == second.PublicId).Should().Match<ScheduleItem>(i =>
            i.Profession == "Nutrition" && i.SendWeekday == "Monday" && i.SendTime == "08:15" && i.AnswerUntilWeekday == "Thursday");
    }

    [Fact]
    public async Task Get_AnswerWindowCrossesMidnightInClientZone_ReturnsWeekdayOfTheLocalEnd()
    {
        var coach = await SubscribedCoachAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        await AddCheckInRuleAsync(coach, DayOfWeek.Thursday, new TimeOnly(22, 0), windowHours: 30);

        var item = (await GetScheduleAsync(client)).Should().ContainSingle().Subject;

        item.SendWeekday.Should().Be("Thursday");
        item.SendTime.Should().Be("22:00");
        item.AnswerUntilWeekday.Should().Be("Saturday");
    }
}
