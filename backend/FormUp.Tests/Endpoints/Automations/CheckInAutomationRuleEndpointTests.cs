using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Automations.Shared;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.Automations;

/// <summary>
/// Integration tests for the check-in rule: a "Send form" rule whose form is a check-in form.
/// </summary>
[Collection(TestCollection.Name)]
public class CheckInAutomationRuleEndpointTests(FitnessApiFactory factory)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private Task<Actor> CreateCoachAsync() => TestActors.Trainer(factory).CreateAsync(Ct);

    /// <summary>A coach on the seeded Studio plan, which includes check-ins and per-client configuration.</summary>
    private async Task<Actor> CreateEntitledCoachAsync()
    {
        var coach = await CreateCoachAsync();
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "studio");
        return coach;
    }

    private static Dictionary<string, object?> CheckInRule(Guid questionnaireId, bool enabled = true) => new()
    {
        ["name"] = "Weekly check-in",
        ["actionType"] = "SendForm",
        ["questionnaireId"] = questionnaireId,
        ["triggerKind"] = "Schedule",
        ["scheduleInterval"] = 1,
        ["scheduleUnit"] = "Weeks",
        ["localTime"] = "18:00:00",
        ["scheduleWeekday"] = "Thursday",
        ["answerWindowHours"] = 48,
        ["reminderAfterHours"] = 24,
        ["notifyCoachOnMissedWeek"] = true,
        ["enabled"] = enabled,
    };

    private static async Task<AutomationRuleDto> ReadRuleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<AutomationRuleDto>(AutomationTestData.Json, Ct))!;

    private static async Task AssertErrorCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(code);
    }

    [Fact]
    public async Task Create_CheckInRuleOnACheckInForm_IsAcceptedAndNeverSkips()
    {
        var coach = await CreateEntitledCoachAsync();
        var formId = await AutomationTestData.CreateQuestionnaireAsync(factory, coach, QuestionnaireType.CheckIn);
        var body = CheckInRule(formId);
        body["skipWhilePreviousUnanswered"] = true;

        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", body, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rule = await ReadRuleAsync(response);
        rule.ScheduleWeekday.Should().Be(DayOfWeek.Thursday);
        rule.AnswerWindowHours.Should().Be(48);
        rule.ReminderAfterHours.Should().Be(24);
        rule.NotifyCoachOnMissedWeek.Should().BeTrue();
        rule.SkipWhilePreviousUnanswered.Should().BeFalse("each check-in week stands on its own");
    }

    [Fact]
    public async Task Create_CheckInRuleWithSevenDayWindow_RoundTrips()
    {
        var coach = await CreateEntitledCoachAsync();
        var formId = await AutomationTestData.CreateQuestionnaireAsync(factory, coach, QuestionnaireType.CheckIn);
        var body = CheckInRule(formId);
        body["answerWindowHours"] = 168;

        var created = await ReadRuleAsync(await coach.Http.PostAsJsonAsync("/trainer/automations/rules", body, Ct));

        created.AnswerWindowHours.Should().Be(168);
        created.ReminderAfterHours.Should().Be(24);
    }

    [Fact]
    public async Task CheckInRule_SecondEnabledOne_IsRefusedOnCreateAndOnEnabling()
    {
        var coach = await CreateEntitledCoachAsync();
        var formId = await AutomationTestData.CreateQuestionnaireAsync(factory, coach, QuestionnaireType.CheckIn);
        (await coach.Http.PostAsJsonAsync("/trainer/automations/rules", CheckInRule(formId), Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", CheckInRule(formId), Ct);
        await AssertErrorCodeAsync(second, HttpStatusCode.Conflict, ErrorCodes.CheckInRuleExists);

        var disabled = await ReadRuleAsync(
            await coach.Http.PostAsJsonAsync("/trainer/automations/rules", CheckInRule(formId, enabled: false), Ct));
        var enable = await coach.Http.PutAsJsonAsync(
            $"/trainer/automations/rules/{disabled.RuleId}", CheckInRule(formId), Ct);
        await AssertErrorCodeAsync(enable, HttpStatusCode.Conflict, ErrorCodes.CheckInRuleExists);
    }

    [Fact]
    public async Task Update_RuleSwitchedToACheckInForm_IsRefusedWhileAnotherIsEnabled()
    {
        var coach = await CreateEntitledCoachAsync();
        var formId = await AutomationTestData.CreateQuestionnaireAsync(factory, coach, QuestionnaireType.CheckIn);
        var otherFormId = await AutomationTestData.CreateQuestionnaireAsync(factory, coach);
        await coach.Http.PostAsJsonAsync("/trainer/automations/rules", CheckInRule(formId), Ct);
        var plainBody = AutomationTestData.ScheduleRule("Plan review");
        plainBody["actionType"] = "SendForm";
        plainBody["questionnaireId"] = otherFormId;
        var plain = await ReadRuleAsync(await coach.Http.PostAsJsonAsync("/trainer/automations/rules", plainBody, Ct));

        var response = await coach.Http.PutAsJsonAsync($"/trainer/automations/rules/{plain.RuleId}", CheckInRule(formId), Ct);

        await AssertErrorCodeAsync(response, HttpStatusCode.Conflict, ErrorCodes.CheckInRuleExists);
    }

    [Fact]
    public async Task Create_CheckInFormWithoutAnswerWindow_Returns400()
    {
        var coach = await CreateCoachAsync();
        var formId = await AutomationTestData.CreateQuestionnaireAsync(factory, coach, QuestionnaireType.CheckIn);
        var body = CheckInRule(formId);
        body["answerWindowHours"] = null;

        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", body, Ct);

        await AssertErrorCodeAsync(response, HttpStatusCode.BadRequest, ErrorCodes.AutomationCheckInInvalid);
    }

    [Fact]
    public async Task Create_CheckInFieldsOnANonCheckInForm_Returns400()
    {
        var coach = await CreateCoachAsync();
        var formId = await AutomationTestData.CreateQuestionnaireAsync(factory, coach);

        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", CheckInRule(formId), Ct);

        await AssertErrorCodeAsync(response, HttpStatusCode.BadRequest, ErrorCodes.AutomationCheckInFieldsNotAllowed);
    }

    [Fact]
    public async Task Override_CheckInRule_TakesAnotherWeekdayAndRefusesCheckInFieldsOnOtherRules()
    {
        var coach = await CreateEntitledCoachAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        var formId = await AutomationTestData.CreateQuestionnaireAsync(factory, coach, QuestionnaireType.CheckIn);
        var rule = await ReadRuleAsync(await coach.Http.PostAsJsonAsync("/trainer/automations/rules", CheckInRule(formId), Ct));
        var photoRule = await ReadRuleAsync(
            await coach.Http.PostAsJsonAsync("/trainer/automations/rules", AutomationTestData.ScheduleRule(), Ct));
        var overrideBody = CheckInRule(formId);
        overrideBody["scheduleWeekday"] = "Monday";

        var accepted = await coach.Http.PutAsJsonAsync(
            $"/trainer/clients/{client.PublicId}/automations/{rule.RuleId}/override", overrideBody, Ct);
        var refused = await coach.Http.PutAsJsonAsync(
            $"/trainer/clients/{client.PublicId}/automations/{photoRule.RuleId}/override", overrideBody, Ct);

        accepted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertErrorCodeAsync(refused, HttpStatusCode.BadRequest, ErrorCodes.AutomationCheckInFieldsNotAllowed);
    }

    [Fact]
    public async Task Create_CheckInRuleWithoutTheCheckInFeature_Returns403()
    {
        var coach = await CreateCoachAsync();
        await CoachSubscriptionBuilder.SubscribeAsync(factory, coach, canUseWeeklyCheckIns: false, canUsePerClientCheckInConfig: true);
        var formId = await AutomationTestData.CreateQuestionnaireAsync(factory, coach, QuestionnaireType.CheckIn);

        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", CheckInRule(formId), Ct);

        await AssertErrorCodeAsync(response, HttpStatusCode.Forbidden, ErrorCodes.SubscriptionRequired);
    }

    [Fact]
    public async Task Override_CheckInRuleWithoutPerClientConfig_Returns403()
    {
        var coach = await CreateCoachAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeAsync(factory, coach, canUseWeeklyCheckIns: true, canUsePerClientCheckInConfig: false);
        var formId = await AutomationTestData.CreateQuestionnaireAsync(factory, coach, QuestionnaireType.CheckIn);
        var rule = await ReadRuleAsync(await coach.Http.PostAsJsonAsync("/trainer/automations/rules", CheckInRule(formId), Ct));
        var overrideBody = CheckInRule(formId);
        overrideBody["scheduleWeekday"] = "Monday";

        var response = await coach.Http.PutAsJsonAsync(
            $"/trainer/clients/{client.PublicId}/automations/{rule.RuleId}/override", overrideBody, Ct);

        await AssertErrorCodeAsync(response, HttpStatusCode.Forbidden, ErrorCodes.SubscriptionRequired);
    }

    [Fact]
    public async Task Create_CheckInRuleWithBothFeatures_IsAllowed()
    {
        var coach = await CreateCoachAsync();
        await CoachSubscriptionBuilder.SubscribeAsync(factory, coach, canUseWeeklyCheckIns: true, canUsePerClientCheckInConfig: true);
        var formId = await AutomationTestData.CreateQuestionnaireAsync(factory, coach, QuestionnaireType.CheckIn);

        var response = await coach.Http.PostAsJsonAsync("/trainer/automations/rules", CheckInRule(formId), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
