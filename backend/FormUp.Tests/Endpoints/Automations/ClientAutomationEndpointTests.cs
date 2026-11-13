using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Automations.GetClientAutomations;
using FormUp.Application.Features.Automations.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Automations;

/// <summary>
/// Integration tests for the per-client automation endpoints under <c>/trainer/clients/{ClientId}/automations</c>.
/// </summary>
[Collection(TestCollection.Name)]
public class ClientAutomationEndpointTests(FitnessApiFactory factory)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private async Task<(Actor Coach, Actor Client, long LinkId)> LinkedPairAsync()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var linkId = await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        return (coach, client, linkId);
    }

    private static async Task<AutomationRuleDto> CreateRuleAsync(Actor coach, Dictionary<string, object?> body)
    {
        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", body, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AutomationRuleDto>(AutomationTestData.Json, Ct))!;
    }

    private static async Task<GetClientAutomationsResponse> ListAsync(Actor coach, Actor client)
    {
        var response = await coach.Http.GetAsync($"/trainer/clients/{client.PublicId}/automations", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<GetClientAutomationsResponse>(AutomationTestData.Json, Ct))!;
    }

    private static string RuleRoute(Actor client, Guid ruleId, string suffix) =>
        $"/trainer/clients/{client.PublicId}/automations/{ruleId}/{suffix}";

    private static Dictionary<string, object?> OverrideBody(int interval = 2) => new()
    {
        ["triggerKind"] = "Schedule",
        ["scheduleInterval"] = interval,
        ["scheduleUnit"] = "Weeks",
        ["localTime"] = "09:00:00",
    };

    // ── list ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_GlobalRuleForAllClients_AppearsAsDefaultWithNextSend()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var rule = await CreateRuleAsync(coach, AutomationTestData.ScheduleRule());

        var list = await ListAsync(coach, client);

        var item = list.Items.Should().ContainSingle(i => i.RuleId == rule.RuleId).Subject;
        item.Source.Should().Be(AutomationRuleSource.Default);
        item.NextSendAt.Should().NotBeNull().And.BeAfter(DateTime.UtcNow);
        item.LastResult.Should().BeNull();
        item.Sent30Days.Should().Be(0);
        list.Last30Days.Sent.Should().Be(0);
    }

    [Fact]
    public async Task List_RulesOfAnotherCoach_AreNotShown()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var other = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CreateRuleAsync(other, AutomationTestData.ScheduleRule("Theirs"));

        var list = await ListAsync(coach, client);

        list.Items.Should().NotContain(i => i.Name == "Theirs");
    }

    [Fact]
    public async Task List_TagAudience_OnlyCoversTaggedClients()
    {
        var (coach, tagged, taggedLink) = await LinkedPairAsync();
        var untagged = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, untagged).CreateAsync(Ct);
        var (tagPublicId, tagId) = await AutomationTestData.CreateTagAsync(factory, coach);
        await AutomationTestData.AssignTagAsync(factory, tagId, taggedLink);
        var body = AutomationTestData.ScheduleRule("Tagged only");
        body["audience"] = "Tag";
        body["audienceTagId"] = tagPublicId;
        await CreateRuleAsync(coach, body);

        (await ListAsync(coach, tagged)).Items.Should().Contain(i => i.Name == "Tagged only");
        (await ListAsync(coach, untagged)).Items.Should().NotContain(i => i.Name == "Tagged only");
    }

    [Fact]
    public async Task List_ChosenAudience_OnlyCoversChosenClients()
    {
        var (coach, chosen, _) = await LinkedPairAsync();
        var other = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, other).CreateAsync(Ct);
        var body = AutomationTestData.ScheduleRule("Chosen only");
        body["audience"] = "Chosen";
        body["audienceClientIds"] = new[] { chosen.PublicId };
        await CreateRuleAsync(coach, body);

        (await ListAsync(coach, chosen)).Items.Should().Contain(i => i.Name == "Chosen only");
        (await ListAsync(coach, other)).Items.Should().NotContain(i => i.Name == "Chosen only");
    }

    [Fact]
    public async Task List_TagAudienceAfterTagDeleted_CoversNobody()
    {
        var (coach, client, linkId) = await LinkedPairAsync();
        var (tagPublicId, tagId) = await AutomationTestData.CreateTagAsync(factory, coach);
        await AutomationTestData.AssignTagAsync(factory, tagId, linkId);
        var body = AutomationTestData.ScheduleRule("Orphaned");
        body["audience"] = "Tag";
        body["audienceTagId"] = tagPublicId;
        await CreateRuleAsync(coach, body);
        await coach.Http.DeleteAsync($"/trainer/client-tags/{tagPublicId}", Ct);

        (await ListAsync(coach, client)).Items.Should().NotContain(i => i.Name == "Orphaned");
    }

    [Fact]
    public async Task List_ClientOnlyRule_AppearsOnlyForThatClient()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var other = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, other).CreateAsync(Ct);

        var created = await coach.Http.PostAsJsonAsync(
            $"/trainer/clients/{client.PublicId}/automations", AutomationTestData.ScheduleRule("Knee check"), Ct);
        created.StatusCode.Should().Be(HttpStatusCode.OK);

        var mine = (await ListAsync(coach, client)).Items.Single(i => i.Name == "Knee check");
        mine.Source.Should().Be(AutomationRuleSource.OnlyThisClient);
        (await ListAsync(coach, other)).Items.Should().NotContain(i => i.Name == "Knee check");
    }

    [Fact]
    public async Task List_WithRunsInTheLog_ReportsLastResultAndThirtyDayCounts()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var rule = await CreateRuleAsync(coach, AutomationTestData.ScheduleRule());
        var now = DateTime.UtcNow;
        await AutomationTestData.AddRunAsync(factory, rule.RuleId, client.ProfileId, AutomationRunStatus.Sent,
            sentAt: now.AddDays(-10), answeredAt: now.AddDays(-9));
        await AutomationTestData.AddRunAsync(factory, rule.RuleId, client.ProfileId, AutomationRunStatus.Sent,
            sentAt: now.AddDays(-3));
        await AutomationTestData.AddRunAsync(factory, rule.RuleId, client.ProfileId, AutomationRunStatus.Skipped,
            AutomationSkipReason.PreviousUnanswered);

        var list = await ListAsync(coach, client);

        var item = list.Items.Single(i => i.RuleId == rule.RuleId);
        item.Sent30Days.Should().Be(2);
        item.Answered30Days.Should().Be(1);
        item.Skipped30Days.Should().Be(1);
        item.LastResult!.Status.Should().Be(AutomationRunStatus.Skipped);
        item.LastResult.SkipReason.Should().Be(AutomationSkipReason.PreviousUnanswered);
        list.Last30Days.Should().BeEquivalentTo(new ClientAutomationSummary { Sent = 2, Answered = 1, Waiting = 1, Skipped = 1 });
    }

    [Fact]
    public async Task List_ClientWithNoLiveLink_Returns404()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var stranger = await TestActors.Client(factory).CreateAsync(Ct);

        var response = await coach.Http.GetAsync($"/trainer/clients/{stranger.PublicId}/automations", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_LinkThatGrantsNothing_Returns403()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CanViewNutritionPlans(false).CanViewTrainingPlans(false).CreateAsync(Ct);

        var response = await coach.Http.GetAsync($"/trainer/clients/{client.PublicId}/automations", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── override / reset ────────────────────────────────────────────────────

    [Fact]
    public async Task Override_ThenList_ShowsCustomWithTheCustomisedSchedule()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var rule = await CreateRuleAsync(coach, AutomationTestData.ScheduleRule());

        var response = await coach.Http.PutAsJsonAsync(RuleRoute(client, rule.RuleId, "override"), OverrideBody(2), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var item = (await ListAsync(coach, client)).Items.Single(i => i.RuleId == rule.RuleId);
        item.Source.Should().Be(AutomationRuleSource.Custom);
        item.ScheduleInterval.Should().Be(2);
        item.Name.Should().Be(rule.Name);
    }

    [Fact]
    public async Task Override_DoesNotTouchTheGlobalRuleOrOtherClients()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var other = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, other).CreateAsync(Ct);
        var rule = await CreateRuleAsync(coach, AutomationTestData.ScheduleRule());

        await coach.Http.PutAsJsonAsync(RuleRoute(client, rule.RuleId, "override"), OverrideBody(1), Ct);

        var otherItem = (await ListAsync(coach, other)).Items.Single(i => i.RuleId == rule.RuleId);
        otherItem.Source.Should().Be(AutomationRuleSource.Default);
        otherItem.ScheduleInterval.Should().Be(4);
        var global = await coach.Http.GetFromJsonAsync<AutomationRuleDto>(
            $"/trainer/automations/rules/{rule.RuleId}", AutomationTestData.Json, Ct);
        global!.ScheduleInterval.Should().Be(4);
    }

    [Fact]
    public async Task Override_InvalidSchedule_Returns400()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var rule = await CreateRuleAsync(coach, AutomationTestData.ScheduleRule());

        var response = await coach.Http.PutAsJsonAsync(RuleRoute(client, rule.RuleId, "override"), OverrideBody(0), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Override_RuleThatDoesNotCoverTheClient_Returns404()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var elsewhere = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, elsewhere).CreateAsync(Ct);
        var body = AutomationTestData.ScheduleRule();
        body["audience"] = "Chosen";
        body["audienceClientIds"] = new[] { elsewhere.PublicId };
        var rule = await CreateRuleAsync(coach, body);

        var response = await coach.Http.PutAsJsonAsync(RuleRoute(client, rule.RuleId, "override"), OverrideBody(), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Override_RuleOfAnotherCoach_Returns404()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var other = await TestActors.Trainer(factory).CreateAsync(Ct);
        var rule = await CreateRuleAsync(other, AutomationTestData.ScheduleRule());

        var response = await coach.Http.PutAsJsonAsync(RuleRoute(client, rule.RuleId, "override"), OverrideBody(), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Override_ClientOnlyRuleOfThisClient_Returns409()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var created = await (await coach.Http.PostAsJsonAsync(
                $"/trainer/clients/{client.PublicId}/automations", AutomationTestData.ScheduleRule(), Ct))
            .Content.ReadFromJsonAsync<AutomationRuleDto>(AutomationTestData.Json, Ct);

        var response = await coach.Http.PutAsJsonAsync(RuleRoute(client, created!.RuleId, "override"), OverrideBody(), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Override_ClientOnlyRuleOfAnotherClient_Returns404()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var other = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, other).CreateAsync(Ct);
        var created = await (await coach.Http.PostAsJsonAsync(
                $"/trainer/clients/{other.PublicId}/automations", AutomationTestData.ScheduleRule(), Ct))
            .Content.ReadFromJsonAsync<AutomationRuleDto>(AutomationTestData.Json, Ct);

        var response = await coach.Http.PutAsJsonAsync(RuleRoute(client, created!.RuleId, "override"), OverrideBody(), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reset_AfterOverride_ReturnsTheRuleToDefault()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var rule = await CreateRuleAsync(coach, AutomationTestData.ScheduleRule());
        await coach.Http.PutAsJsonAsync(RuleRoute(client, rule.RuleId, "override"), OverrideBody(), Ct);

        var response = await coach.Http.DeleteAsync(RuleRoute(client, rule.RuleId, "override"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var item = (await ListAsync(coach, client)).Items.Single(i => i.RuleId == rule.RuleId);
        item.Source.Should().Be(AutomationRuleSource.Default);
        item.ScheduleInterval.Should().Be(4);
    }

    [Fact]
    public async Task Reset_WithNothingToReset_Returns409()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var rule = await CreateRuleAsync(coach, AutomationTestData.ScheduleRule());

        var response = await coach.Http.DeleteAsync(RuleRoute(client, rule.RuleId, "override"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Reset_ClearsAPauseToo()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var rule = await CreateRuleAsync(coach, AutomationTestData.ScheduleRule());
        await coach.Http.PutAsJsonAsync(RuleRoute(client, rule.RuleId, "pause"), new { isPaused = true }, Ct);

        await coach.Http.DeleteAsync(RuleRoute(client, rule.RuleId, "override"), Ct);

        (await ListAsync(coach, client)).Items.Single(i => i.RuleId == rule.RuleId)
            .Source.Should().Be(AutomationRuleSource.Default);
    }

    // ── pause ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pause_WithEndDateAndNote_ShowsPausedWithNoNextSend()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var rule = await CreateRuleAsync(coach, AutomationTestData.ScheduleRule());
        var until = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10);

        var response = await coach.Http.PutAsJsonAsync(RuleRoute(client, rule.RuleId, "pause"),
            new { isPaused = true, pausedUntil = until.ToString("yyyy-MM-dd"), pauseNote = "knee injury" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var item = (await ListAsync(coach, client)).Items.Single(i => i.RuleId == rule.RuleId);
        item.Source.Should().Be(AutomationRuleSource.Paused);
        item.IsPaused.Should().BeTrue();
        item.PausedUntil.Should().Be(until);
        item.PauseNote.Should().Be("knee injury");
        item.NextSendAt.Should().BeNull();
    }

    [Fact]
    public async Task Pause_WithLapsedEndDate_ReportsTheRuleAsNotPausedWithoutAnyWrite()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var rule = await CreateRuleAsync(coach, AutomationTestData.ScheduleRule());
        await coach.Http.PutAsJsonAsync(RuleRoute(client, rule.RuleId, "pause"), new { isPaused = true }, Ct);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await db.AutomationClientOverrides.SingleAsync(o => o.ClientProfileId == client.ProfileId, Ct);
            stored.PausedUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);
            await db.SaveChangesAsync(Ct);
        }

        var item = (await ListAsync(coach, client)).Items.Single(i => i.RuleId == rule.RuleId);

        item.IsPaused.Should().BeFalse();
        item.Source.Should().Be(AutomationRuleSource.Default);
        item.PausedUntil.Should().BeNull();
        item.NextSendAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Pause_EndDateInThePast_Returns400()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var rule = await CreateRuleAsync(coach, AutomationTestData.ScheduleRule());
        var past = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-5);

        var response = await coach.Http.PutAsJsonAsync(RuleRoute(client, rule.RuleId, "pause"),
            new { isPaused = true, pausedUntil = past.ToString("yyyy-MM-dd") }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Pause_NoteWithoutPause_Returns400()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var rule = await CreateRuleAsync(coach, AutomationTestData.ScheduleRule());

        var response = await coach.Http.PutAsJsonAsync(RuleRoute(client, rule.RuleId, "pause"),
            new { isPaused = false, pauseNote = "oops" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Resume_OfAPausedRule_RemovesTheEmptyOverride()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var rule = await CreateRuleAsync(coach, AutomationTestData.ScheduleRule());
        await coach.Http.PutAsJsonAsync(RuleRoute(client, rule.RuleId, "pause"), new { isPaused = true }, Ct);

        var response = await coach.Http.PutAsJsonAsync(RuleRoute(client, rule.RuleId, "pause"), new { isPaused = false }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ListAsync(coach, client)).Items.Single(i => i.RuleId == rule.RuleId)
            .Source.Should().Be(AutomationRuleSource.Default);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.AutomationClientOverrides.CountAsync(o => o.ClientProfileId == client.ProfileId, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Resume_KeepsACustomisation()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var rule = await CreateRuleAsync(coach, AutomationTestData.ScheduleRule());
        await coach.Http.PutAsJsonAsync(RuleRoute(client, rule.RuleId, "override"), OverrideBody(2), Ct);
        await coach.Http.PutAsJsonAsync(RuleRoute(client, rule.RuleId, "pause"), new { isPaused = true }, Ct);

        await coach.Http.PutAsJsonAsync(RuleRoute(client, rule.RuleId, "pause"), new { isPaused = false }, Ct);

        var item = (await ListAsync(coach, client)).Items.Single(i => i.RuleId == rule.RuleId);
        item.Source.Should().Be(AutomationRuleSource.Custom);
        item.ScheduleInterval.Should().Be(2);
    }

    [Fact]
    public async Task Pause_ClientOnlyRule_Returns409()
    {
        var (coach, client, _) = await LinkedPairAsync();
        var created = await (await coach.Http.PostAsJsonAsync(
                $"/trainer/clients/{client.PublicId}/automations", AutomationTestData.ScheduleRule(), Ct))
            .Content.ReadFromJsonAsync<AutomationRuleDto>(AutomationTestData.Json, Ct);

        var response = await coach.Http.PutAsJsonAsync(RuleRoute(client, created!.RuleId, "pause"), new { isPaused = true }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Pause_ClientWithNoLiveLink_Returns404()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var stranger = await TestActors.Client(factory).CreateAsync(Ct);
        var rule = await CreateRuleAsync(coach, AutomationTestData.ScheduleRule());

        var response = await coach.Http.PutAsJsonAsync(RuleRoute(stranger, rule.RuleId, "pause"), new { isPaused = true }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── client-only rule creation ───────────────────────────────────────────

    [Fact]
    public async Task CreateClientOnly_ForClientWithNoLiveLink_Returns404()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var stranger = await TestActors.Client(factory).CreateAsync(Ct);

        var response = await coach.Http.PostAsJsonAsync(
            $"/trainer/clients/{stranger.PublicId}/automations", AutomationTestData.ScheduleRule(), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateClientOnly_ReturnsRuleBoundToTheClient()
    {
        var (coach, client, _) = await LinkedPairAsync();

        var response = await coach.Http.PostAsJsonAsync(
            $"/trainer/clients/{client.PublicId}/automations", AutomationTestData.ScheduleRule("Knee check"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rule = await response.Content.ReadFromJsonAsync<AutomationRuleDto>(AutomationTestData.Json, Ct);
        rule!.OnlyClientId.Should().Be(client.PublicId);
    }

    [Fact]
    public async Task Override_CheckInWindowWithMismatchedReminder_Returns400()
    {
        var (coach, client, _) = await LinkedPairAsync();
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "studio");
        var formId = await AutomationTestData.CreateQuestionnaireAsync(factory, coach, QuestionnaireType.CheckIn);
        var rule = await CreateRuleAsync(coach, new Dictionary<string, object?>
        {
            ["name"] = "Weekly check-in",
            ["actionType"] = "SendForm",
            ["questionnaireId"] = formId,
            ["triggerKind"] = "Schedule",
            ["scheduleInterval"] = 1,
            ["scheduleUnit"] = "Weeks",
            ["localTime"] = "18:00:00",
            ["scheduleWeekday"] = "Thursday",
            ["answerWindowHours"] = 48,
        });
        var overrideBody = new Dictionary<string, object?>
        {
            ["name"] = "Weekly check-in", ["actionType"] = "SendForm", ["questionnaireId"] = formId, ["enabled"] = true,
            ["triggerKind"] = "Schedule",
            ["scheduleInterval"] = 1,
            ["scheduleUnit"] = "Weeks",
            ["localTime"] = "18:00:00",
            ["scheduleWeekday"] = "Monday",
            ["answerWindowHours"] = 48,
            ["reminderAfterHours"] = 12,
        };

        var response = await coach.Http.PutAsJsonAsync(RuleRoute(client, rule.RuleId, "override"), overrideBody, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(FormUp.Application.Domain.Constants.ErrorCodes.AutomationCheckInInvalid);
    }

    [Fact]
    public async Task List_CheckInOverrideIgnoredWhenThePlanLacksPerClientConfig()
    {
        var (coach, client, _) = await LinkedPairAsync();
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "studio");
        var formId = await AutomationTestData.CreateQuestionnaireAsync(factory, coach, QuestionnaireType.CheckIn);
        var rule = await CreateRuleAsync(coach, new Dictionary<string, object?>
        {
            ["name"] = "Weekly check-in",
            ["actionType"] = "SendForm",
            ["questionnaireId"] = formId,
            ["triggerKind"] = "Schedule",
            ["scheduleInterval"] = 1,
            ["scheduleUnit"] = "Weeks",
            ["localTime"] = "18:00:00",
            ["scheduleWeekday"] = "Thursday",
            ["answerWindowHours"] = 48,
        });
        var overrideBody = new Dictionary<string, object?>
        {
            ["name"] = "Weekly check-in", ["actionType"] = "SendForm", ["questionnaireId"] = formId, ["reminderAfterHours"] = 24, ["notifyCoachOnMissedWeek"] = true, ["enabled"] = true,
            ["triggerKind"] = "Schedule",
            ["scheduleInterval"] = 1,
            ["scheduleUnit"] = "Weeks",
            ["localTime"] = "18:00:00",
            ["scheduleWeekday"] = "Monday",
            ["answerWindowHours"] = 48,
        };
        (await coach.Http.PutAsJsonAsync(RuleRoute(client, rule.RuleId, "override"), overrideBody, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ListAsync(coach, client)).Items.Single(i => i.RuleId == rule.RuleId)
            .ScheduleWeekday.Should().Be(DayOfWeek.Monday, "an entitled coach's override shows");

        await CoachSubscriptionBuilder.SubscribeAsync(
            factory, coach, canUseWeeklyCheckIns: true, canUsePerClientCheckInConfig: false);

        var item = (await ListAsync(coach, client)).Items.Single(i => i.RuleId == rule.RuleId);
        item.ScheduleWeekday.Should().Be(DayOfWeek.Thursday);
        item.AnswerWindowHours.Should().Be(48);
        item.Source.Should().Be(AutomationRuleSource.Default);
    }
}
