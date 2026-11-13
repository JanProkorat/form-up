using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.SubscriptionPlans.GetSubscriptionPlans;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.SubscriptionPlans;

/// <summary>
/// Integration tests for <c>GET /admin/subscription-plans</c>
/// (<see cref="Application.Features.SubscriptionPlans.GetSubscriptionPlans.GetSubscriptionPlansEndpoint"/>).
/// Uses <see cref="FitnessApiFactory"/> since the role gate (Admin-only, and Admin is not
/// publicly registerable) can only be proven through the real ASP.NET Core pipeline.
/// </summary>
[Collection(TestCollection.Name)]
public class GetSubscriptionPlansEndpointTests(FitnessApiFactory factory)
{
    // The API serializes enums as strings (JsonStringEnumConverter globally), so use matching
    // options when deserializing the test response — see the same pattern in
    // CreateMealTemplateEndpointTests.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task List_AsAdmin_ReturnsAllPlansIncludingInactive()
    {
        var activeCode = $"active-{Guid.NewGuid():N}";
        var inactiveCode = $"inactive-{Guid.NewGuid():N}";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.SubscriptionPlans.AddRange(
                MakePlan(activeCode, isActive: true),
                MakePlan(inactiveCode, isActive: false));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);

        var response = await client.GetAsync(
            "/admin/subscription-plans", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<GetSubscriptionPlansResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.Plans.Should().Contain(p => p.Code == activeCode && p.IsActive);
        body.Plans.Should().Contain(p => p.Code == inactiveCode && !p.IsActive);
    }

    [Fact]
    public async Task List_ReturnsPerPlanCoachCountOfLiveSubscriptionsOnly()
    {
        var code = $"counted-{Guid.NewGuid():N}";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.SubscriptionPlans.Add(MakePlan(code, isActive: true));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        foreach (var status in new[]
                 {
                     SubscriptionStatus.Trialing, SubscriptionStatus.Active, SubscriptionStatus.PastDue,
                     SubscriptionStatus.Canceled, SubscriptionStatus.Incomplete,
                 })
        {
            var coach = await TestActors.Trainer(factory).CreateAsync();
            await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, code, status);
        }

        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);

        var response = await client.GetAsync(
            "/admin/subscription-plans", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<GetSubscriptionPlansResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.Plans.Single(p => p.Code == code).CoachCount.Should().Be(3);
    }

    private static SubscriptionPlan MakePlan(string code, bool isActive)
    {
        var plan = new SubscriptionPlan
        {
            Code = code,
            NameCs = "Test",
            NameEn = "Test",
            NameDe = "Test",
            BothRolesIncluded = true,
            Currency = "CZK",
            PriceMinorUnits = 0,
            BillingInterval = BillingInterval.Monthly,
            IsActive = isActive,
        };
        SubscriptionPlanPriceBuilder.OpenPriceFor(plan);
        return plan;
    }
}
