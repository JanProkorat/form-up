using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.NutritionPlanTemplates;

/// <summary>
/// A coach whose plan lacks plan templates cannot touch any nutrition plan template endpoint; instantiating also
/// still needs plan creation.
/// </summary>
[Collection(TestCollection.Name)]
public class NutritionPlanTemplatesEntitlementTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Actor> NutritionistAsync(Action<FormUp.Application.Domain.Entities.SubscriptionPlan> configure)
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.MoveToPlanAsync(factory, coach, configure);

        return coach;
    }

    private async Task<Guid> SeedTemplateAsync(Guid ownerId)
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

    [Fact]
    public async Task TemplateEndpoints_PlanWithoutPlanTemplates_Return403SubscriptionRequired()
    {
        var coach = await NutritionistAsync(plan => plan.CanUsePlanTemplates = false);
        var templateId = await SeedTemplateAsync(coach.UserId);

        var responses = new Dictionary<string, HttpResponseMessage>
        {
            ["GET /nutrition/plan-templates"] = await coach.Http.GetAsync("/nutrition/plan-templates", Ct),
            ["GET /nutrition/plan-templates/{id}"] = await coach.Http.GetAsync($"/nutrition/plan-templates/{templateId}", Ct),
            ["DELETE /nutrition/plan-templates/{id}"] = await coach.Http.DeleteAsync($"/nutrition/plan-templates/{templateId}", Ct),
            ["POST /nutrition/plan-templates/{id}/copy"] = await coach.Http.PostAsJsonAsync(
                $"/nutrition/plan-templates/{templateId}/copy", new { }, Ct),
        };

        foreach (var (call, response) in responses)
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, call);
            (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.SubscriptionRequired, call);
        }
    }

    [Fact]
    public async Task TemplateList_PlanWithPlanTemplates_ReachesTheEndpoint()
    {
        var coach = await NutritionistAsync(_ => { });

        var response = await coach.Http.GetAsync("/nutrition/plan-templates", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Instantiate_PlanWithoutPlanTemplatesButWithPlans_Returns403SubscriptionRequired()
    {
        var coach = await NutritionistAsync(plan => plan.CanUsePlanTemplates = false);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).AsRole(UserRole.Nutritionist).CreateAsync(Ct);
        var templateId = await SeedTemplateAsync(coach.UserId);

        var response = await coach.Http.PostAsJsonAsync(
            $"/nutrition/plan-templates/{templateId}/instantiate", new { ClientId = client.PublicId, Name = "Plan" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.SubscriptionRequired);
    }

    [Fact]
    public async Task Instantiate_PlanWithPlanTemplatesButWithoutPlans_StillReturns403SubscriptionRequired()
    {
        var coach = await NutritionistAsync(plan => plan.CanCreatePlans = false);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).AsRole(UserRole.Nutritionist).CreateAsync(Ct);
        var templateId = await SeedTemplateAsync(coach.UserId);

        var response = await coach.Http.PostAsJsonAsync(
            $"/nutrition/plan-templates/{templateId}/instantiate", new { ClientId = client.PublicId, Name = "Plan" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.SubscriptionRequired);
    }
}
