using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Automations.GetAutomationRules;
using FormUp.Application.Features.Automations.GetClientAutomations;
using FormUp.Application.Features.Automations.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Automations;

/// <summary>
/// Integration tests for the rule summary and the plan-event next send.
/// Plan dates run against the real Mongo container because the mock ignores filters.
/// </summary>
[Collection(TestCollection.Shard3)]
public class AutomationSummaryEndpointTests(FitnessApiFactory factory)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly TimeZoneInfo Prague = ClientLocalDateResolver.ResolveTimeZone("Europe/Prague");

    private static DateTime PlanStart() => DateTime.UtcNow.Date.AddDays(10);

    private static DateTime LocalAtUtc(DateTime startDate, int addDays, int hour = 9) =>
        AutomationScheduleCalculator.ToUtc(DateOnly.FromDateTime(startDate).AddDays(addDays), new TimeOnly(hour, 0), Prague);

    private async Task<(Actor Coach, Actor Client)> LinkedPairAsync()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        return (coach, client);
    }

    private static async Task<AutomationRuleDto> CreateRuleAsync(Actor coach, Dictionary<string, object?> body)
    {
        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", body, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AutomationRuleDto>(AutomationTestData.Json, Ct))!;
    }

    private static async Task<GetAutomationRulesResponse> GetRulesAsync(Actor coach)
    {
        var response = await coach.Http.GetAsync("/trainer/automations/rules", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<GetAutomationRulesResponse>(AutomationTestData.Json, Ct))!;
    }

    private static async Task<AutomationRuleSummaryDto> SummaryOfAsync(Actor coach, Guid ruleId) =>
        (await GetRulesAsync(coach)).Rules.Single(r => r.RuleId == ruleId).Summary!;

    private static async Task<ClientAutomationItem> ClientItemAsync(Actor coach, Actor client, Guid ruleId)
    {
        var list = await coach.Http.GetFromJsonAsync<GetClientAutomationsResponse>(
            $"/trainer/clients/{client.PublicId}/automations", AutomationTestData.Json, Ct);
        return list!.Items.Single(i => i.RuleId == ruleId);
    }

    private static Dictionary<string, object?> EventRule(
        AutomationEventType eventType, int offset, AutomationOffsetDirection direction, string? localTime = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = "Plan event",
            ["actionType"] = "RequestMeasurements",
            ["triggerKind"] = "Event",
            ["eventType"] = eventType.ToString(),
            ["eventOffsetDays"] = offset,
            ["eventOffsetDirection"] = direction.ToString(),
        };

        if (localTime is not null)
        {
            body["localTime"] = localTime;
        }

        return body;
    }

    private static string OverrideRoute(Actor client, Guid ruleId) =>
        $"/trainer/clients/{client.PublicId}/automations/{ruleId}/override";

    // ── summary counts ──────────────────────────────────────────────────────

    [Fact]
    public async Task Rules_Summary_CountsCoveredClientsCustomOverridesAndPauses()
    {
        var (coach, custom) = await LinkedPairAsync();
        var paused = await TestActors.Client(factory).CreateAsync(Ct);
        var plain = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, paused).CreateAsync(Ct);
        await TestActors.Link(factory, coach, plain).CreateAsync(Ct);
        var rule = await CreateRuleAsync(coach, AutomationTestData.ScheduleRule());

        await coach.Http.PutAsJsonAsync(OverrideRoute(custom, rule.RuleId), new
        {
            triggerKind = "Schedule", scheduleInterval = 2, scheduleUnit = "Weeks", localTime = "09:00:00",
        }, Ct);
        await coach.Http.PutAsJsonAsync(
            $"/trainer/clients/{paused.PublicId}/automations/{rule.RuleId}/pause", new { isPaused = true }, Ct);

        var summary = await SummaryOfAsync(coach, rule.RuleId);

        summary.ClientCount.Should().Be(3);
        summary.CustomCount.Should().Be(1);
        summary.PausedCount.Should().Be(1);
        summary.NextSendAt.Should().NotBeNull().And.BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task Rules_Summary_NextSendIsTheEarliestAcrossClientsAndMatchesTheClientList()
    {
        var (coach, daily) = await LinkedPairAsync();
        var other = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, other).CreateAsync(Ct);
        var rule = await CreateRuleAsync(coach, AutomationTestData.ScheduleRule());
        await coach.Http.PutAsJsonAsync(OverrideRoute(daily, rule.RuleId), new
        {
            triggerKind = "Schedule", scheduleInterval = 1, scheduleUnit = "Days", localTime = "09:00:00",
        }, Ct);

        var summary = await SummaryOfAsync(coach, rule.RuleId);

        var dailyItem = await ClientItemAsync(coach, daily, rule.RuleId);
        var otherItem = await ClientItemAsync(coach, other, rule.RuleId);
        dailyItem.NextSendAt.Should().BeBefore(otherItem.NextSendAt!.Value);
        summary.NextSendAt.Should().Be(dailyItem.NextSendAt);
    }

    [Fact]
    public async Task Rules_Summary_DisabledRuleHasNoNextSendButStillCountsClients()
    {
        var (coach, _) = await LinkedPairAsync();
        var body = AutomationTestData.ScheduleRule();
        body["enabled"] = false;
        var rule = await CreateRuleAsync(coach, body);

        var summary = await SummaryOfAsync(coach, rule.RuleId);

        summary.ClientCount.Should().Be(1);
        summary.NextSendAt.Should().BeNull();
    }

    [Fact]
    public async Task Rules_Summary_LinkThatGrantsNothingOrEndedIsNotCovered()
    {
        var (coach, _) = await LinkedPairAsync();
        var noAccess = await TestActors.Client(factory).CreateAsync(Ct);
        var ended = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, noAccess).CanViewNutritionPlans(false).CanViewTrainingPlans(false).CreateAsync(Ct);
        await TestActors.Link(factory, coach, ended).Inactive().CreateAsync(Ct);
        var rule = await CreateRuleAsync(coach, AutomationTestData.ScheduleRule());

        var summary = await SummaryOfAsync(coach, rule.RuleId);

        summary.ClientCount.Should().Be(1);
    }

    [Fact]
    public async Task Rules_Summary_TagAndChosenAudiencesCountOnlyTheirClients()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var tagged = await TestActors.Client(factory).CreateAsync(Ct);
        var untagged = await TestActors.Client(factory).CreateAsync(Ct);
        var taggedLinkId = await TestActors.Link(factory, coach, tagged).CreateAsync(Ct);
        await TestActors.Link(factory, coach, untagged).CreateAsync(Ct);
        var (tagPublicId, tagId) = await AutomationTestData.CreateTagAsync(factory, coach);
        await AutomationTestData.AssignTagAsync(factory, tagId, taggedLinkId);

        var tagBody = AutomationTestData.ScheduleRule("Tagged");
        tagBody["audience"] = "Tag";
        tagBody["audienceTagId"] = tagPublicId;
        var tagRule = await CreateRuleAsync(coach, tagBody);

        var chosenBody = AutomationTestData.ScheduleRule("Chosen");
        chosenBody["audience"] = "Chosen";
        chosenBody["audienceClientIds"] = new[] { tagged.PublicId, untagged.PublicId };
        var chosenRule = await CreateRuleAsync(coach, chosenBody);

        var rules = await GetRulesAsync(coach);

        rules.Rules.Single(r => r.RuleId == tagRule.RuleId).Summary!.ClientCount.Should().Be(1);
        rules.Rules.Single(r => r.RuleId == chosenRule.RuleId).Summary!.ClientCount.Should().Be(2);
    }

    [Fact]
    public async Task GetRule_ReturnsTheSummary_WhileCreateAndUpdateReturnNone()
    {
        var (coach, _) = await LinkedPairAsync();
        var created = await CreateRuleAsync(coach, AutomationTestData.ScheduleRule());

        created.Summary.Should().BeNull();

        var single = await coach.Http.GetFromJsonAsync<AutomationRuleDto>(
            $"/trainer/automations/rules/{created.RuleId}", AutomationTestData.Json, Ct);
        single!.Summary.Should().NotBeNull();
        single.Summary!.ClientCount.Should().Be(1);

        var update = AutomationTestData.ScheduleRule("Renamed");
        var updated = await coach.Http.PutAsJsonAsync($"/trainer/automations/rules/{created.RuleId}", update, Ct);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await updated.Content.ReadFromJsonAsync<AutomationRuleDto>(AutomationTestData.Json, Ct))!.Summary.Should().BeNull();
    }

    // ── event send time ─────────────────────────────────────────────────────

    [Fact]
    public async Task EventRule_WithoutLocalTime_IsAcceptedAndStoresNone()
    {
        var (coach, _) = await LinkedPairAsync();

        var rule = await CreateRuleAsync(coach, EventRule(AutomationEventType.PlanStarted, 1, AutomationOffsetDirection.After));

        rule.LocalTime.Should().BeNull();
    }

    [Fact]
    public async Task EventRule_WithLocalTime_KeepsIt()
    {
        var (coach, _) = await LinkedPairAsync();

        var rule = await CreateRuleAsync(
            coach, EventRule(AutomationEventType.PlanEnded, 1, AutomationOffsetDirection.Before, "18:30:00"));

        rule.LocalTime.Should().Be(new TimeSpan(18, 30, 0));
        (await SummaryOfAsync(coach, rule.RuleId)).ClientCount.Should().Be(1);
    }

    [Fact]
    public async Task EventRule_WithLocalTimeOutsideTheDay_Returns400()
    {
        var (coach, _) = await LinkedPairAsync();

        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules",
            EventRule(AutomationEventType.PlanStarted, 0, AutomationOffsetDirection.After, "24:00:00"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── plan-event next send ────────────────────────────────────────────────

    [Fact]
    public async Task PlanStarted_NextSendIsOffsetAfterTheStartAtNineInTheClientZone_OnBothSurfaces()
    {
        var (coach, client) = await LinkedPairAsync();
        var start = PlanStart();
        await AutomationTestData.SeedTrainingPlanAsync(factory, client.UserId, coach.UserId, start, 2);
        var rule = await CreateRuleAsync(coach, EventRule(AutomationEventType.PlanStarted, 2, AutomationOffsetDirection.After));

        var summary = await SummaryOfAsync(coach, rule.RuleId);
        var item = await ClientItemAsync(coach, client, rule.RuleId);

        summary.NextSendAt.Should().Be(LocalAtUtc(start, 2));
        item.NextSendAt.Should().Be(summary.NextSendAt);
    }

    [Fact]
    public async Task PlanEnded_NextSendCountsFromTheLastDayAndHonoursBeforeAndTheRuleTime()
    {
        var (coach, client) = await LinkedPairAsync();
        var start = PlanStart();
        await AutomationTestData.SeedTrainingPlanAsync(factory, client.UserId, coach.UserId, start, 2);
        var rule = await CreateRuleAsync(
            coach, EventRule(AutomationEventType.PlanEnded, 1, AutomationOffsetDirection.Before, "18:00:00"));

        var summary = await SummaryOfAsync(coach, rule.RuleId);

        // Two weeks end on day 13 after the start; one day before is day 12.
        summary.NextSendAt.Should().Be(LocalAtUtc(start, 12, 18));
    }

    [Fact]
    public async Task PlanEvent_SeveralClients_TheEarliestWins()
    {
        var (coach, later) = await LinkedPairAsync();
        var sooner = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, sooner).CreateAsync(Ct);
        var start = PlanStart();
        await AutomationTestData.SeedTrainingPlanAsync(factory, later.UserId, coach.UserId, start.AddDays(14), 2);
        await AutomationTestData.SeedNutritionPlanAsync(factory, sooner.UserId, coach.UserId, start, 2);
        var rule = await CreateRuleAsync(coach, EventRule(AutomationEventType.PlanStarted, 0, AutomationOffsetDirection.After));

        (await SummaryOfAsync(coach, rule.RuleId)).NextSendAt.Should().Be(LocalAtUtc(start, 0));
    }

    [Fact]
    public async Task PlanEvent_AnotherCoachsPlanOrANonActiveOrUnscheduledPlan_NeverDrivesTheDate()
    {
        var (coach, client) = await LinkedPairAsync();
        var otherCoach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        await AutomationTestData.SeedTrainingPlanAsync(factory, client.UserId, otherCoach.UserId, PlanStart(), 2);
        await AutomationTestData.SeedTrainingPlanAsync(
            factory, client.UserId, coach.UserId, PlanStart(), 2, TrainingPlanStatus.Draft);
        await AutomationTestData.SeedTrainingPlanAsync(factory, client.UserId, coach.UserId, null, 2);
        var rule = await CreateRuleAsync(coach, EventRule(AutomationEventType.PlanStarted, 0, AutomationOffsetDirection.After));

        (await SummaryOfAsync(coach, rule.RuleId)).NextSendAt.Should().BeNull();
        (await ClientItemAsync(coach, client, rule.RuleId)).NextSendAt.Should().BeNull();
    }

    [Fact]
    public async Task PlanEvent_PlanInThePast_HasNoNextSend()
    {
        var (coach, client) = await LinkedPairAsync();
        await AutomationTestData.SeedTrainingPlanAsync(factory, client.UserId, coach.UserId, DateTime.UtcNow.Date.AddDays(-30), 2);
        var rule = await CreateRuleAsync(coach, EventRule(AutomationEventType.PlanStarted, 0, AutomationOffsetDirection.After));

        (await SummaryOfAsync(coach, rule.RuleId)).NextSendAt.Should().BeNull();
    }

    [Fact]
    public async Task PlanEvent_TrainingPlanNeedsTheTrainingCapabilityAndNutritionPlanTheNutritionOne()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var nutritionOnly = await TestActors.Client(factory).CreateAsync(Ct);
        var trainingOnly = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, nutritionOnly).CanViewTrainingPlans(false).CanViewNutritionPlans().CreateAsync(Ct);
        await TestActors.Link(factory, coach, trainingOnly).CanViewNutritionPlans(false).CanViewTrainingPlans().CreateAsync(Ct);
        var start = PlanStart();
        await AutomationTestData.SeedTrainingPlanAsync(factory, nutritionOnly.UserId, coach.UserId, start, 2);
        await AutomationTestData.SeedNutritionPlanAsync(factory, trainingOnly.UserId, coach.UserId, start, 2);
        var rule = await CreateRuleAsync(coach, EventRule(AutomationEventType.PlanStarted, 0, AutomationOffsetDirection.After));

        (await SummaryOfAsync(coach, rule.RuleId)).NextSendAt.Should().BeNull();

        await AutomationTestData.SeedNutritionPlanAsync(factory, nutritionOnly.UserId, coach.UserId, start, 2);

        (await SummaryOfAsync(coach, rule.RuleId)).NextSendAt.Should().Be(LocalAtUtc(start, 0));
        (await ClientItemAsync(coach, trainingOnly, rule.RuleId)).NextSendAt.Should().BeNull();
    }

    [Theory]
    [InlineData(AutomationEventType.ClientJoined)]
    [InlineData(AutomationEventType.CheckInMissed)]
    public async Task OtherEventTypes_HaveNoNextSend(AutomationEventType eventType)
    {
        var (coach, client) = await LinkedPairAsync();
        await AutomationTestData.SeedTrainingPlanAsync(factory, client.UserId, coach.UserId, PlanStart(), 2);
        var rule = await CreateRuleAsync(coach, EventRule(eventType, 0, AutomationOffsetDirection.After));

        (await SummaryOfAsync(coach, rule.RuleId)).NextSendAt.Should().BeNull();
    }

    // ── photo message limit ─────────────────────────────────────────────────

    [Fact]
    public async Task Override_ProgressPhotosMessageOver500_Returns400WithTheCode()
    {
        var (coach, client) = await LinkedPairAsync();
        var rule = await CreateRuleAsync(coach, AutomationTestData.ScheduleRule());
        var body = new Dictionary<string, object?>
        {
            ["triggerKind"] = "Schedule", ["scheduleInterval"] = 2, ["scheduleUnit"] = "Weeks", ["localTime"] = "09:00:00",
            ["message"] = new string('m', 501),
        };

        var tooLong = await coach.Http.PutAsJsonAsync(OverrideRoute(client, rule.RuleId), body, Ct);

        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await tooLong.Content.ReadAsStringAsync(Ct)).Should().Contain("AUTOMATION_PHOTO_MESSAGE_TOO_LONG");

        body["message"] = new string('m', 500);
        var fits = await coach.Http.PutAsJsonAsync(OverrideRoute(client, rule.RuleId), body, Ct);

        fits.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Override_OtherActionWithLongMessage_StillPasses()
    {
        var (coach, client) = await LinkedPairAsync();
        var ruleBody = AutomationTestData.ScheduleRule();
        ruleBody["actionType"] = "RequestMeasurements";
        var rule = await CreateRuleAsync(coach, ruleBody);

        var response = await coach.Http.PutAsJsonAsync(OverrideRoute(client, rule.RuleId), new Dictionary<string, object?>
        {
            ["triggerKind"] = "Schedule", ["scheduleInterval"] = 2, ["scheduleUnit"] = "Weeks", ["localTime"] = "09:00:00",
            ["message"] = new string('m', 1000),
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Create_ProgressPhotosMessageOver500_Returns400()
    {
        var (coach, _) = await LinkedPairAsync();
        var body = AutomationTestData.ScheduleRule();
        body["message"] = new string('m', 501);

        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", body, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
