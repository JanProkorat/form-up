using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.CoachSubscriptions;

/// <summary>
/// The two template-instantiate endpoints answer 403 SUBSCRIPTION_REQUIRED for a coach whose plan lacks plan creation,
/// but only after the template and the client link resolved: a missing template still answers 404.
/// </summary>
[Collection(TestCollection.Name)]
public class InstantiateEntitlementGateIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Actor Coach, Actor Client)> LinkedPairWithoutPlanFeaturesAsync(UserRole role)
    {
        var coach = role == UserRole.Nutritionist
            ? await TestActors.Nutritionist(factory).CreateAsync(Ct)
            : await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeAsync(
            factory, coach, canUseWeeklyCheckIns: false, canUsePerClientCheckInConfig: false);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).AsRole(role).CreateAsync(Ct);

        return (coach, client);
    }

    private async Task<Guid> SeedNutritionTemplateAsync(Guid ownerId)
    {
        var template = new NutritionPlanTemplate
        {
            ExternalId = Guid.NewGuid(),
            OwnerId = ownerId,
            Name = "Gate template",
            Visibility = LibraryVisibility.Private,
            Version = 1,
            DateCreated = DateTime.UtcNow,
            WeekCount = 1,
            Weeks = [new TemplateWeek { WeekNumber = 1, Days = [] }],
        };

        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMongoContext>()
            .NutritionPlanTemplates.InsertOneAsync(template, cancellationToken: Ct);

        return template.ExternalId;
    }

    private async Task<Guid> SeedTrainingTemplateAsync(Guid ownerId)
    {
        var template = new TrainingPlanTemplateBuilder()
            .WithOwnerId(ownerId)
            .WithVisibility(LibraryVisibility.Private)
            .Build();

        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMongoContext>()
            .TrainingPlanTemplates.InsertOneAsync(template, cancellationToken: Ct);

        return template.ExternalId;
    }

    private static async Task AssertSubscriptionRequiredAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.SubscriptionRequired);
    }

    [Fact]
    public async Task InstantiateNutritionTemplate_PlanWithoutCanCreatePlans_Returns403SubscriptionRequired()
    {
        var (coach, client) = await LinkedPairWithoutPlanFeaturesAsync(UserRole.Nutritionist);
        var templateId = await SeedNutritionTemplateAsync(coach.UserId);

        var response = await coach.Http.PostAsJsonAsync(
            $"/nutrition/plan-templates/{templateId}/instantiate", new { ClientId = client.PublicId, Name = "Plan" }, Ct);

        await AssertSubscriptionRequiredAsync(response);
    }

    [Fact]
    public async Task InstantiateNutritionTemplate_MissingTemplateOnAPlanWithoutCanCreatePlans_StillReturns404()
    {
        var (coach, client) = await LinkedPairWithoutPlanFeaturesAsync(UserRole.Nutritionist);

        var response = await coach.Http.PostAsJsonAsync(
            $"/nutrition/plan-templates/{Guid.NewGuid()}/instantiate", new { ClientId = client.PublicId, Name = "Plan" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task InstantiateTrainingTemplate_PlanWithoutCanCreatePlans_Returns403SubscriptionRequired()
    {
        var (coach, client) = await LinkedPairWithoutPlanFeaturesAsync(UserRole.Trainer);
        var templateId = await SeedTrainingTemplateAsync(coach.UserId);

        var response = await coach.Http.PostAsJsonAsync(
            $"/training/plan-templates/{templateId}/instantiate", new { ClientId = client.PublicId, Name = "Plan" }, Ct);

        await AssertSubscriptionRequiredAsync(response);
    }

    [Fact]
    public async Task InstantiateTrainingTemplate_MissingTemplateOnAPlanWithoutCanCreatePlans_StillReturns404()
    {
        var (coach, client) = await LinkedPairWithoutPlanFeaturesAsync(UserRole.Trainer);

        var response = await coach.Http.PostAsJsonAsync(
            $"/training/plan-templates/{Guid.NewGuid()}/instantiate", new { ClientId = client.PublicId, Name = "Plan" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
