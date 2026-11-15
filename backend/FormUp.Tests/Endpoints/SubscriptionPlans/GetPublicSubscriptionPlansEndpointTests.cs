using System.Net;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.SubscriptionPlans;

/// <summary>
/// Integration tests for <c>GET /subscription-plans</c>.
/// </summary>
[Collection(TestCollection.Name)]
public class GetPublicSubscriptionPlansEndpointTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] ForbiddenFields =
        ["isActive", "id", "publicId", "externalPriceId", "prices", "coachCount", "coaches", "taxMode"];

    [Fact]
    public async Task Get_Anonymous_ReturnsOfferedPlansWithCacheHeader_AndSkipsInactiveOnes()
    {
        var offeredCode = await PaidPlanBuilder.CreateAsync(factory, priceMinorUnits: 12300);
        var inactiveCode = $"inactive-{Guid.NewGuid():N}"[..30];

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var plan = new SubscriptionPlan
            {
                Code = inactiveCode,
                NameCs = "Skrytý",
                NameEn = "Hidden",
                NameDe = "Versteckt",
                Currency = "CZK",
                IsActive = false,
            };
            SubscriptionPlanPriceBuilder.OpenPriceFor(plan);
            db.SubscriptionPlans.Add(plan);
            await db.SaveChangesAsync(Ct);
        }

        var response = await factory.CreateClient().GetAsync("/subscription-plans", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Public.Should().BeTrue();
        response.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(300));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var plans = document.RootElement.GetProperty("plans").EnumerateArray().ToList();
        var codes = plans.Select(plan => plan.GetProperty("code").GetString()).ToList();

        codes.Should().Contain(offeredCode);
        codes.Should().NotContain(inactiveCode);

        var offered = plans.Single(plan => plan.GetProperty("code").GetString() == offeredCode);
        offered.GetProperty("priceMinorUnits").GetInt64().Should().Be(12300);
        offered.GetProperty("secondRoleAddOnPriceMinorUnits").ValueKind.Should().Be(JsonValueKind.Null);
        offered.GetProperty("bothRolesIncluded").GetBoolean().Should().BeTrue();
        offered.GetProperty("isMostPopular").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
    }

    [Fact]
    public async Task Get_Anonymous_ExposesNoInternalFields()
    {
        await PaidPlanBuilder.CreateAsync(factory);

        var response = await factory.CreateClient().GetAsync("/subscription-plans", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        foreach (var plan in document.RootElement.GetProperty("plans").EnumerateArray())
        {
            var names = plan.EnumerateObject().Select(property => property.Name).ToList();

            names.Should().NotIntersectWith(ForbiddenFields);
        }

        document.RootElement.EnumerateObject().Select(property => property.Name).Should().NotIntersectWith(ForbiddenFields);
    }
}
