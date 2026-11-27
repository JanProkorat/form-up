using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Automations.GetAutomationRules;
using FormUp.Application.Features.Automations.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Automations;

/// <summary>
/// Integration tests for the global automation rule endpoints under <c>/trainer/automations/rules</c>.
/// </summary>
[Collection(TestCollection.Shard3)]
public class AutomationRuleEndpointTests(FitnessApiFactory factory)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private Task<Actor> CreateCoachAsync() => TestActors.Trainer(factory).CreateAsync(Ct);

    private static async Task<AutomationRuleDto> ReadRuleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<AutomationRuleDto>(AutomationTestData.Json, Ct))!;

    [Fact]
    public async Task Create_ValidScheduleRule_ReturnsRuleWithDefaults()
    {
        var coach = await CreateCoachAsync();

        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", AutomationTestData.ScheduleRule(), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rule = await ReadRuleAsync(response);
        rule.Name.Should().Be("Progress photos");
        rule.ActionType.Should().Be(AutomationActionType.RequestProgressPhotos);
        rule.Audience.Should().Be(AutomationAudience.All);
        rule.SkipWhilePreviousUnanswered.Should().BeTrue();
        rule.Enabled.Should().BeTrue();
        rule.LocalTime.Should().Be(TimeSpan.FromHours(9));
        rule.OnlyClientId.Should().BeNull();
    }

    [Theory]
    [InlineData("CheckInMissed", 1)]
    [InlineData("ClientInactive", 7)]
    [InlineData("FormAnswered", 0)]
    public async Task Create_RuleWithNewEventTrigger_RoundTrips(string eventType, int offset)
    {
        var coach = await CreateCoachAsync();
        var body = AutomationTestData.ScheduleRule("Event rule");
        body["triggerKind"] = "Event";
        body["eventType"] = eventType;
        body["eventOffsetDays"] = offset;
        body["scheduleInterval"] = null;
        body["scheduleUnit"] = null;
        body["localTime"] = null;

        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", body, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var created = await ReadRuleAsync(response);
        var reread = await ReadRuleAsync(await coach.Http.GetAsync($"/trainer/automations/rules/{created.RuleId}", Ct));
        reread.TriggerKind.Should().Be(AutomationTriggerKind.Event);
        reread.EventType.Should().Be(Enum.Parse<AutomationEventType>(eventType));
        reread.EventOffsetDays.Should().Be(offset);
        reread.EventOffsetDirection.Should().Be(AutomationOffsetDirection.After);
        reread.ScheduleInterval.Should().BeNull();
    }

    [Fact]
    public async Task Create_InvalidBody_Returns400()
    {
        var coach = await CreateCoachAsync();
        var body = AutomationTestData.ScheduleRule();
        body["scheduleInterval"] = 0;

        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", body, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_SendFormWithOwnQuestionnaire_Succeeds()
    {
        var coach = await CreateCoachAsync();
        var questionnaireId = await AutomationTestData.CreateQuestionnaireAsync(factory, coach);
        var body = AutomationTestData.ScheduleRule();
        body["actionType"] = "SendForm";
        body["questionnaireId"] = questionnaireId;

        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", body, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadRuleAsync(response)).QuestionnaireId.Should().Be(questionnaireId);
    }

    [Fact]
    public async Task Create_SendFormWithForeignQuestionnaire_Returns404()
    {
        var coach = await CreateCoachAsync();
        var other = await CreateCoachAsync();
        var foreignQuestionnaire = await AutomationTestData.CreateQuestionnaireAsync(factory, other);
        var body = AutomationTestData.ScheduleRule();
        body["actionType"] = "SendForm";
        body["questionnaireId"] = foreignQuestionnaire;

        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", body, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_SendFormWithInactiveOwnQuestionnaire_Returns400()
    {
        var coach = await CreateCoachAsync();
        var questionnaireId = await AutomationTestData.CreateQuestionnaireAsync(
            factory, coach, QuestionnaireType.Onboarding, isActive: false);
        var body = AutomationTestData.ScheduleRule();
        body["actionType"] = "SendForm";
        body["questionnaireId"] = questionnaireId;

        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", body, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.AutomationQuestionnaireNotUsable);
    }

    [Fact]
    public async Task Update_SendFormWithInactiveOwnQuestionnaire_Returns400()
    {
        var coach = await CreateCoachAsync();
        var usableId = await AutomationTestData.CreateQuestionnaireAsync(factory, coach);
        var createBody = AutomationTestData.ScheduleRule();
        createBody["actionType"] = "SendForm";
        createBody["questionnaireId"] = usableId;
        var created = await ReadRuleAsync(await coach.Http.PostAsJsonAsync("/trainer/automations/rules", createBody, Ct));
        var questionnaireId = await AutomationTestData.CreateQuestionnaireAsync(
            factory, coach, QuestionnaireType.Onboarding, isActive: false);

        var update = AutomationTestData.ScheduleRule();
        update["actionType"] = "SendForm";
        update["questionnaireId"] = questionnaireId;
        var response = await coach.Http.PutAsJsonAsync($"/trainer/automations/rules/{created.RuleId}", update, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.AutomationQuestionnaireNotUsable);
    }

    [Fact]
    public async Task Create_TagAudienceWithForeignTag_Returns404()
    {
        var coach = await CreateCoachAsync();
        var other = await CreateCoachAsync();
        var (foreignTag, _) = await AutomationTestData.CreateTagAsync(factory, other);
        var body = AutomationTestData.ScheduleRule();
        body["audience"] = "Tag";
        body["audienceTagId"] = foreignTag;

        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", body, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_TagAudienceWithUnknownTag_Returns404()
    {
        var coach = await CreateCoachAsync();
        var body = AutomationTestData.ScheduleRule();
        body["audience"] = "Tag";
        body["audienceTagId"] = Guid.NewGuid();

        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", body, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_TagAudienceWithOwnTag_ReturnsTheTag()
    {
        var coach = await CreateCoachAsync();
        var (tagId, _) = await AutomationTestData.CreateTagAsync(factory, coach, "Online");
        var body = AutomationTestData.ScheduleRule();
        body["audience"] = "Tag";
        body["audienceTagId"] = tagId;

        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", body, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rule = await ReadRuleAsync(response);
        rule.Audience.Should().Be(AutomationAudience.Tag);
        rule.Tag.Should().NotBeNull();
        rule.Tag!.TagId.Should().Be(tagId);
        rule.Tag.Name.Should().Be("Online");
    }

    [Fact]
    public async Task Create_ChosenAudienceWithUnlinkedClient_Returns404AndWritesNothing()
    {
        var coach = await CreateCoachAsync();
        var linked = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, linked).CreateAsync(Ct);
        var stranger = await TestActors.Client(factory).CreateAsync(Ct);
        var body = AutomationTestData.ScheduleRule("Should not exist");
        body["audience"] = "Chosen";
        body["audienceClientIds"] = new[] { linked.PublicId, stranger.PublicId };

        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", body, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var list = await (await coach.Http.GetAsync("/trainer/automations/rules", Ct))
            .Content.ReadFromJsonAsync<GetAutomationRulesResponse>(AutomationTestData.Json, Ct);
        list!.Rules.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_ChosenAudienceWithInactiveLink_Returns404()
    {
        var coach = await CreateCoachAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).Inactive().CreateAsync(Ct);
        var body = AutomationTestData.ScheduleRule();
        body["audience"] = "Chosen";
        body["audienceClientIds"] = new[] { client.PublicId };

        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", body, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_ChosenAudienceWithLinkedClients_ReturnsThem()
    {
        var coach = await CreateCoachAsync();
        var client = await TestActors.Client(factory).WithName("Eva", "Svobodova").CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        var body = AutomationTestData.ScheduleRule();
        body["audience"] = "Chosen";
        body["audienceClientIds"] = new[] { client.PublicId };

        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", body, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rule = await ReadRuleAsync(response);
        rule.Clients.Should().ContainSingle(c => c.ClientId == client.PublicId && c.FirstName == "Eva");
    }

    [Fact]
    public async Task List_ReturnsOnlyTheCallersGlobalRules()
    {
        var coach = await CreateCoachAsync();
        var other = await CreateCoachAsync();
        await coach.Http.PostAsJsonAsync("/trainer/automations/rules", AutomationTestData.ScheduleRule("Mine"), Ct);
        await other.Http.PostAsJsonAsync("/trainer/automations/rules", AutomationTestData.ScheduleRule("Theirs"), Ct);

        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        await coach.Http.PostAsJsonAsync($"/trainer/clients/{client.PublicId}/automations",
            AutomationTestData.ScheduleRule("Client only"), Ct);

        var list = await (await coach.Http.GetAsync("/trainer/automations/rules", Ct))
            .Content.ReadFromJsonAsync<GetAutomationRulesResponse>(AutomationTestData.Json, Ct);

        list!.Rules.Select(r => r.Name).Should().BeEquivalentTo(["Mine"]);
    }

    [Fact]
    public async Task Get_RuleOfAnotherCoach_Returns404()
    {
        var coach = await CreateCoachAsync();
        var other = await CreateCoachAsync();
        var created = await ReadRuleAsync(
            await other.Http.PostAsJsonAsync("/trainer/automations/rules", AutomationTestData.ScheduleRule(), Ct));

        var response = await coach.Http.GetAsync($"/trainer/automations/rules/{created.RuleId}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_UnknownRule_Returns404()
    {
        var coach = await CreateCoachAsync();

        var response = await coach.Http.GetAsync($"/trainer/automations/rules/{Guid.NewGuid()}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_OwnRule_ReplacesFieldsAndKeepsAudienceWhenOmitted()
    {
        var coach = await CreateCoachAsync();
        var (tagId, _) = await AutomationTestData.CreateTagAsync(factory, coach);
        var createBody = AutomationTestData.ScheduleRule();
        createBody["audience"] = "Tag";
        createBody["audienceTagId"] = tagId;
        var created = await ReadRuleAsync(await coach.Http.PostAsJsonAsync("/trainer/automations/rules", createBody, Ct));

        var update = AutomationTestData.ScheduleRule("Renamed");
        update["scheduleInterval"] = 2;
        update["message"] = "Please send them";
        update["enabled"] = false;
        var response = await coach.Http.PutAsJsonAsync($"/trainer/automations/rules/{created.RuleId}", update, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rule = await ReadRuleAsync(response);
        rule.Name.Should().Be("Renamed");
        rule.ScheduleInterval.Should().Be(2);
        rule.Message.Should().Be("Please send them");
        rule.Enabled.Should().BeFalse();
        rule.Audience.Should().Be(AutomationAudience.Tag);
        rule.Tag!.TagId.Should().Be(tagId);
    }

    [Fact]
    public async Task Update_ChosenAudience_DiffsTheClientList()
    {
        var coach = await CreateCoachAsync();
        var first = await TestActors.Client(factory).CreateAsync(Ct);
        var second = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, first).CreateAsync(Ct);
        await TestActors.Link(factory, coach, second).CreateAsync(Ct);

        var createBody = AutomationTestData.ScheduleRule();
        createBody["audience"] = "Chosen";
        createBody["audienceClientIds"] = new[] { first.PublicId };
        var created = await ReadRuleAsync(await coach.Http.PostAsJsonAsync("/trainer/automations/rules", createBody, Ct));

        var update = AutomationTestData.ScheduleRule();
        update["audience"] = "Chosen";
        update["audienceClientIds"] = new[] { first.PublicId, second.PublicId };
        var widened = await ReadRuleAsync(
            await coach.Http.PutAsJsonAsync($"/trainer/automations/rules/{created.RuleId}", update, Ct));
        widened.Clients.Select(c => c.ClientId).Should().BeEquivalentTo([first.PublicId, second.PublicId]);

        update["audienceClientIds"] = new[] { second.PublicId };
        var narrowed = await ReadRuleAsync(
            await coach.Http.PutAsJsonAsync($"/trainer/automations/rules/{created.RuleId}", update, Ct));
        narrowed.Clients.Select(c => c.ClientId).Should().BeEquivalentTo([second.PublicId]);
    }

    [Fact]
    public async Task Update_RuleOfAnotherCoach_Returns404AndChangesNothing()
    {
        var coach = await CreateCoachAsync();
        var other = await CreateCoachAsync();
        var created = await ReadRuleAsync(
            await other.Http.PostAsJsonAsync("/trainer/automations/rules", AutomationTestData.ScheduleRule("Theirs"), Ct));

        var response = await coach.Http.PutAsJsonAsync(
            $"/trainer/automations/rules/{created.RuleId}", AutomationTestData.ScheduleRule("Hijacked"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var reread = await ReadRuleAsync(await other.Http.GetAsync($"/trainer/automations/rules/{created.RuleId}", Ct));
        reread.Name.Should().Be("Theirs");
    }

    [Fact]
    public async Task Update_ChangingTheActionType_Returns409AndKeepsTheAction()
    {
        var coach = await CreateCoachAsync();
        var created = await ReadRuleAsync(
            await coach.Http.PostAsJsonAsync("/trainer/automations/rules", AutomationTestData.ScheduleRule(), Ct));

        var update = AutomationTestData.ScheduleRule();
        update["actionType"] = "RequestMeasurements";
        var response = await coach.Http.PutAsJsonAsync($"/trainer/automations/rules/{created.RuleId}", update, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.AutomationActionTypeLocked);
        var reread = await ReadRuleAsync(await coach.Http.GetAsync($"/trainer/automations/rules/{created.RuleId}", Ct));
        reread.ActionType.Should().Be(AutomationActionType.RequestProgressPhotos);
    }

    [Fact]
    public async Task Update_TagAudienceWithForeignTag_Returns404()
    {
        var coach = await CreateCoachAsync();
        var other = await CreateCoachAsync();
        var (foreignTag, _) = await AutomationTestData.CreateTagAsync(factory, other);
        var created = await ReadRuleAsync(
            await coach.Http.PostAsJsonAsync("/trainer/automations/rules", AutomationTestData.ScheduleRule(), Ct));

        var update = AutomationTestData.ScheduleRule();
        update["audience"] = "Tag";
        update["audienceTagId"] = foreignTag;
        var response = await coach.Http.PutAsJsonAsync($"/trainer/automations/rules/{created.RuleId}", update, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_ClientOnlyRuleWithAudience_Returns400()
    {
        var coach = await CreateCoachAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        var created = await ReadRuleAsync(await coach.Http.PostAsJsonAsync(
            $"/trainer/clients/{client.PublicId}/automations", AutomationTestData.ScheduleRule(), Ct));

        var update = AutomationTestData.ScheduleRule();
        update["audience"] = "All";
        var response = await coach.Http.PutAsJsonAsync($"/trainer/automations/rules/{created.RuleId}", update, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_ClientOnlyRuleWithoutAudience_Succeeds()
    {
        var coach = await CreateCoachAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        var created = await ReadRuleAsync(await coach.Http.PostAsJsonAsync(
            $"/trainer/clients/{client.PublicId}/automations", AutomationTestData.ScheduleRule(), Ct));

        var response = await coach.Http.PutAsJsonAsync(
            $"/trainer/automations/rules/{created.RuleId}", AutomationTestData.ScheduleRule("Renamed"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadRuleAsync(response)).Name.Should().Be("Renamed");
    }

    [Fact]
    public async Task Delete_OwnRule_RemovesItAndItsOverrides()
    {
        var coach = await CreateCoachAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        var created = await ReadRuleAsync(
            await coach.Http.PostAsJsonAsync("/trainer/automations/rules", AutomationTestData.ScheduleRule(), Ct));
        await coach.Http.PutAsJsonAsync(
            $"/trainer/clients/{client.PublicId}/automations/{created.RuleId}/pause", new { isPaused = true }, Ct);

        var response = await coach.Http.DeleteAsync($"/trainer/automations/rules/{created.RuleId}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await coach.Http.GetAsync($"/trainer/automations/rules/{created.RuleId}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.AutomationClientOverrides.CountAsync(o => o.ClientProfileId == client.ProfileId, Ct))
            .Should().Be(0);
    }

    [Fact]
    public async Task Delete_RuleOfAnotherCoach_Returns404AndKeepsIt()
    {
        var coach = await CreateCoachAsync();
        var other = await CreateCoachAsync();
        var created = await ReadRuleAsync(
            await other.Http.PostAsJsonAsync("/trainer/automations/rules", AutomationTestData.ScheduleRule(), Ct));

        var response = await coach.Http.DeleteAsync($"/trainer/automations/rules/{created.RuleId}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.Http.GetAsync($"/trainer/automations/rules/{created.RuleId}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteTag_LeavesTheRuleWithNoTag()
    {
        var coach = await CreateCoachAsync();
        var (tagId, _) = await AutomationTestData.CreateTagAsync(factory, coach);
        var body = AutomationTestData.ScheduleRule();
        body["audience"] = "Tag";
        body["audienceTagId"] = tagId;
        var created = await ReadRuleAsync(await coach.Http.PostAsJsonAsync("/trainer/automations/rules", body, Ct));

        var deleted = await coach.Http.DeleteAsync($"/trainer/client-tags/{tagId}", Ct);
        deleted.IsSuccessStatusCode.Should().BeTrue();

        var rule = await ReadRuleAsync(await coach.Http.GetAsync($"/trainer/automations/rules/{created.RuleId}", Ct));
        rule.Audience.Should().Be(AutomationAudience.Tag);
        rule.Tag.Should().BeNull("a deleted tag leaves the rule in place covering nobody");
    }
}
