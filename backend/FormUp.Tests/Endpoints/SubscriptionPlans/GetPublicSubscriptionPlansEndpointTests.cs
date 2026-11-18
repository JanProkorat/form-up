using System.Net;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.SubscriptionPlans;

/// <summary>
/// Integration tests for <c>GET /subscription-plans</c>.
/// </summary>
[Collection(TestCollection.Name)]
public class GetPublicSubscriptionPlansEndpointTests(FitnessApiFactory factory) : IDisposable
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

    [Fact]
    public async Task Get_ActiveAutomaticPromotion_ReturnsItWithDiscountedPricesOnCoveredPlansOnly()
    {
        await PromotionBuilder.ClearAsync(factory);
        var covered = await PaidPlanBuilder.CreateAsync(factory, priceMinorUnits: 29900, addOnMinorUnits: 9900, bothRolesIncluded: false);
        var bothRoles = await PaidPlanBuilder.CreateAsync(factory, priceMinorUnits: 49900);
        var uncovered = await PaidPlanBuilder.CreateAsync(factory, priceMinorUnits: 19900, addOnMinorUnits: 5000, bothRolesIncluded: false);
        var promotion = await PromotionBuilder.CreateAsync(factory, [covered, bothRoles], PromotionMode.Automatic, configured =>
        {
            configured.Value = 30;
            configured.Duration = PromotionDuration.Months;
            configured.DurationMonths = 3;
            configured.ClaimUntil = DateTimeOffset.UtcNow.AddDays(20);
        });

        using var document = await GetJsonAsync(factory.CreateClient());

        var promotionJson = document.RootElement.GetProperty("promotion");
        promotionJson.GetProperty("name").GetString().Should().Be(promotion.Name);
        promotionJson.GetProperty("value").GetInt64().Should().Be(30);
        promotionJson.GetProperty("durationMonths").GetInt32().Should().Be(3);
        promotionJson.GetProperty("planCodes").EnumerateArray().Select(code => code.GetString())
            .Should().BeEquivalentTo(covered, bothRoles);

        var plans = document.RootElement.GetProperty("plans").EnumerateArray().ToList();
        var coveredPlan = plans.Single(plan => plan.GetProperty("code").GetString() == covered);
        coveredPlan.GetProperty("discountedPriceMinorUnits").GetInt64().Should().Be(20930);
        coveredPlan.GetProperty("discountedSecondRoleAddOnPriceMinorUnits").GetInt64().Should().Be(6930);

        var bothRolesPlan = plans.Single(plan => plan.GetProperty("code").GetString() == bothRoles);
        bothRolesPlan.GetProperty("discountedPriceMinorUnits").GetInt64().Should().Be(34930);
        bothRolesPlan.GetProperty("discountedSecondRoleAddOnPriceMinorUnits").ValueKind.Should().Be(JsonValueKind.Null);

        var uncoveredPlan = plans.Single(plan => plan.GetProperty("code").GetString() == uncovered);
        uncoveredPlan.GetProperty("discountedPriceMinorUnits").ValueKind.Should().Be(JsonValueKind.Null);
        uncoveredPlan.GetProperty("discountedSecondRoleAddOnPriceMinorUnits").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Get_NoPromotion_ReturnsNullPromotionAndNoDiscountedPrices()
    {
        await PromotionBuilder.ClearAsync(factory);
        await PaidPlanBuilder.CreateAsync(factory);

        using var document = await GetJsonAsync(factory.CreateClient());

        document.RootElement.GetProperty("promotion").ValueKind.Should().Be(JsonValueKind.Null);
        document.RootElement.GetProperty("plans").EnumerateArray()
            .Should().OnlyContain(plan => plan.GetProperty("discountedPriceMinorUnits").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Get_PromotionsThatAreNotOpenAutomaticOffers_AreNotReturned()
    {
        await PromotionBuilder.ClearAsync(factory);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await PromotionBuilder.CreateAsync(factory, [plan]);
        await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Automatic, scheduled =>
            scheduled.ClaimFrom = DateTimeOffset.UtcNow.AddDays(2));
        await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Automatic, ended =>
            ended.EndedAt = DateTimeOffset.UtcNow.AddHours(-1));
        await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Automatic, closed =>
            closed.ClaimUntil = DateTimeOffset.UtcNow.AddHours(-1));
        var usedUp = await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Automatic, limited => limited.MaxUses = 1);
        var redeemer = await TestActors.Trainer(factory).CreateAsync(Ct);
        await PromotionBuilder.AddRedemptionAsync(factory, usedUp.Id, redeemer.ProfileId);

        using var document = await GetJsonAsync(factory.CreateClient());

        document.RootElement.GetProperty("promotion").ValueKind.Should().Be(JsonValueKind.Null);
        document.RootElement.GetProperty("plans").EnumerateArray()
            .Should().OnlyContain(item => item.GetProperty("discountedPriceMinorUnits").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Get_SignedInCoach_GetsTheSameBodyAsAnonymous_AndExposesNoPromotionInternals()
    {
        await PromotionBuilder.ClearAsync(factory);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Automatic, configured =>
        {
            configured.NewCoachesOnly = true;
            configured.OneUsePerCoach = true;
            configured.MaxUses = 50;
        });
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await PromotionBuilder.MarkPaidBeforeAsync(factory, coach.ProfileId);

        var anonymousBody = await (await factory.CreateClient().GetAsync("/subscription-plans", Ct)).Content.ReadAsStringAsync(Ct);
        var signedInBody = await (await coach.Http.GetAsync("/subscription-plans", Ct)).Content.ReadAsStringAsync(Ct);

        signedInBody.Should().Be(anonymousBody);

        using var document = JsonDocument.Parse(anonymousBody);
        var promotionNames = document.RootElement.GetProperty("promotion").EnumerateObject().Select(property => property.Name).ToList();
        promotionNames.Should().BeEquivalentTo(
            "name", "kind", "value", "currency", "duration", "durationMonths", "claimUntil", "planCodes");
        anonymousBody.Should().NotContainAny("stripe", "maxUses", "usedCount", "newCoachesOnly", "oneUsePerCoach", "\"code\":\"CODE");
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client)
    {
        var response = await client.GetAsync("/subscription-plans", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Leaves the promotions table empty for the next test class.</summary>
    public void Dispose() => PromotionBuilder.ClearAsync(factory).GetAwaiter().GetResult();
}
