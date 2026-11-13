using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.Automations;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Client.Collaborations;

/// <summary>
/// Integration tests for the check-in days of <c>GET /client/collaborations</c>, which come from the
/// coach's enabled check-in rule that covers the client.
/// </summary>
[Collection(TestCollection.Name)]
public class GetCollaborationsCheckInIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record CollaborationsResult(List<CollaborationItem> Collaborations);

    private record CollaborationItem(Guid ProfessionalPublicId, List<CheckInItem> CheckIns);

    private record CheckInItem(string Profession, string DayOfWeek);

    private async Task<(Actor Coach, Actor Client)> LinkedPairAsync(UserRole role = UserRole.Trainer)
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).AsRole(role).CreateAsync(Ct);

        return (coach, client);
    }

    private async Task<AutomationRule> AddCheckInRuleAsync(
        Actor coach, DayOfWeek day, bool enabled = true, Action<AutomationRule>? configure = null)
    {
        var form = await AutomationTestData.InsertQuestionnaireAsync(factory, coach, QuestionnaireType.CheckIn);

        return await AutomationTestData.InsertRuleAsync(factory, coach, rule =>
        {
            rule.ActionType = AutomationActionType.SendForm;
            rule.QuestionnaireId = form.Id;
            rule.ScheduleUnit = AutomationScheduleUnit.Weeks;
            rule.ScheduleWeekday = day;
            rule.AnswerWindowHours = 48;
            rule.Enabled = enabled;
            configure?.Invoke(rule);
        });
    }

    private async Task AddOverrideAsync(AutomationRule rule, Actor client, DayOfWeek? day, bool enabled = true)
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
            LocalTime = new TimeOnly(9, 0),
            ScheduleWeekday = day,
            AnswerWindowHours = 48,
            Enabled = enabled,
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<List<CheckInItem>> GetCheckInsAsync(Actor client, Actor coach)
    {
        var response = await client.Http.GetAsync("/client/collaborations", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CollaborationsResult>(cancellationToken: Ct);

        return body!.Collaborations.Single(c => c.ProfessionalPublicId == coach.PublicId).CheckIns;
    }

    [Fact]
    public async Task GetAll_RuleEnabledWithoutOverride_ReturnsRuleDayForTheLinkProfession()
    {
        var (coach, client) = await LinkedPairAsync(UserRole.Nutritionist);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "studio");
        await AddCheckInRuleAsync(coach, DayOfWeek.Wednesday);

        var checkIns = await GetCheckInsAsync(client, coach);

        checkIns.Should().ContainSingle().Which.Should().Be(new CheckInItem("Nutrition", "Wednesday"));
    }

    [Fact]
    public async Task GetAll_OverrideHasDay_OverrideDayWins()
    {
        var (coach, client) = await LinkedPairAsync();
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "studio");
        var rule = await AddCheckInRuleAsync(coach, DayOfWeek.Wednesday);
        await AddOverrideAsync(rule, client, DayOfWeek.Friday);

        var checkIns = await GetCheckInsAsync(client, coach);

        checkIns.Should().ContainSingle().Which.DayOfWeek.Should().Be("Friday");
    }

    [Fact]
    public async Task GetAll_OverrideWhilePerClientConfigIsNotInThePlan_FallsBackToTheRuleDay()
    {
        var (coach, client) = await LinkedPairAsync();
        await CoachSubscriptionBuilder.SubscribeAsync(factory, coach, canUseWeeklyCheckIns: true, canUsePerClientCheckInConfig: false);
        var rule = await AddCheckInRuleAsync(coach, DayOfWeek.Wednesday);
        await AddOverrideAsync(rule, client, DayOfWeek.Friday);

        var checkIns = await GetCheckInsAsync(client, coach);

        checkIns.Should().ContainSingle().Which.DayOfWeek.Should().Be("Wednesday");
    }

    [Fact]
    public async Task GetAll_RuleDisabledOrOverrideDisablesIt_ReturnsNoEntry()
    {
        var (coach, client) = await LinkedPairAsync();
        await AddCheckInRuleAsync(coach, DayOfWeek.Wednesday, enabled: false);

        (await GetCheckInsAsync(client, coach)).Should().BeEmpty();

        var (otherCoach, otherClient) = await LinkedPairAsync();
        var rule = await AddCheckInRuleAsync(otherCoach, DayOfWeek.Wednesday);
        await AddOverrideAsync(rule, otherClient, null, enabled: false);

        (await GetCheckInsAsync(otherClient, otherCoach)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetAll_CoachWithoutTheCheckInFeature_ReturnsNoEntry()
    {
        var (coach, client) = await LinkedPairAsync();
        await CoachSubscriptionBuilder.SubscribeAsync(factory, coach, canUseWeeklyCheckIns: false, canUsePerClientCheckInConfig: false);
        await AddCheckInRuleAsync(coach, DayOfWeek.Wednesday);

        (await GetCheckInsAsync(client, coach)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetAll_NoCheckInRule_ReturnsNoEntry()
    {
        var (coach, client) = await LinkedPairAsync();

        (await GetCheckInsAsync(client, coach)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetAll_ClientOnlyRuleOfAnotherClient_IsNotShown()
    {
        var (coach, client) = await LinkedPairAsync();
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "studio");
        var otherClient = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, otherClient).CreateAsync(Ct);
        await AddCheckInRuleAsync(coach, DayOfWeek.Saturday, configure: rule => rule.ClientProfileId = otherClient.ProfileId);

        (await GetCheckInsAsync(client, coach)).Should().BeEmpty();
        (await GetCheckInsAsync(otherClient, coach)).Should().ContainSingle().Which.DayOfWeek.Should().Be("Saturday");
    }
}
