using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Features.SubscriptionPlans.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.SubscriptionPlans;

/// <summary>
/// Integration tests for <c>POST /admin/subscription-plans</c>
/// (<see cref="Application.Features.SubscriptionPlans.CreateSubscriptionPlan.CreateSubscriptionPlanEndpoint"/>).
/// Uses <see cref="FitnessApiFactory"/> since Admin is not publicly self-registerable — the
/// only way to prove the role gate is the real ASP.NET Core pipeline.
/// </summary>
[Collection(TestCollection.Name)]
public class CreateSubscriptionPlanEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string UniqueCode() => $"tier-{Guid.NewGuid():N}"[..30];

    private static CreatePlanPayload ValidPayload(string code) => new(
        Code: code,
        NameCs: "Malý",
        NameEn: "Small",
        NameDe: "Klein",
        BothRolesIncluded: true,
        SecondRoleAddOnPriceMinorUnits: null,
        CanCreatePlans: true,
        CanMessage: true,
        CanSendQuestionnaires: false,
        CanUseWeeklyCheckIns: false,
        CanUsePerClientCheckInConfig: false,
        CanUsePayments: true,
        CanUsePlanTemplates: true,
        CanUseRecipes: false,
        CanUseIngredients: true,
        CanUseProgressPhotos: false,
        CanUseWeightAndMeasurements: true,
        CanUseAutomations: false,
        CanUsePackages: true,
        MaxActiveClients: 10,
        PriceMinorUnits: 29900,
        Currency: "CZK",
        BillingInterval: "Monthly",
        ExternalPriceId: null,
        IsActive: true);

    [Fact]
    public async Task Create_ValidRequest_PersistsPlan()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = UniqueCode();

        var response = await client.PostAsJsonAsync(
            "/admin/subscription-plans", ValidPayload(code), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionPlanDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Code.Should().Be(code);
        body.PriceMinorUnits.Should().Be(29900);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.SubscriptionPlans
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Code == code, TestContext.Current.CancellationToken);

        persisted.Should().NotBeNull();
        persisted!.NameEn.Should().Be("Small");
        persisted.BothRolesIncluded.Should().BeTrue();
        persisted.SecondRoleAddOnPriceMinorUnits.Should().BeNull();
        persisted.IsActive.Should().BeTrue();
        persisted.CanUsePayments.Should().BeTrue();
        persisted.CanUsePlanTemplates.Should().BeTrue();
        persisted.CanUseRecipes.Should().BeFalse();
        persisted.CanUseIngredients.Should().BeTrue();
        persisted.CanUseProgressPhotos.Should().BeFalse();
        persisted.CanUseWeightAndMeasurements.Should().BeTrue();
        persisted.CanUseAutomations.Should().BeFalse();
        persisted.CanUsePackages.Should().BeTrue();
        body.CanUsePackages.Should().BeTrue();
        body.CanUseRecipes.Should().BeFalse();
    }

    [Fact]
    public async Task Create_PerClientCheckInsWithoutWeeklyCheckIns_Returns400()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);

        var response = await client.PostAsJsonAsync(
            "/admin/subscription-plans",
            ValidPayload(UniqueCode()) with { CanUsePerClientCheckInConfig = true, CanUseWeeklyCheckIns = false },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_WithAddOnPrice_WritesOneOpenPriceRowMirroringThePlan()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = UniqueCode();

        var response = await client.PostAsJsonAsync(
            "/admin/subscription-plans",
            ValidPayload(code) with
            {
                BothRolesIncluded = false,
                SecondRoleAddOnPriceMinorUnits = 14900,
                ExternalPriceId = "price_create",
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionPlanDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.SecondRoleAddOnPriceMinorUnits.Should().Be(14900);
        body.BothRolesIncluded.Should().BeFalse();
        body.ExternalPriceId.Should().Be("price_create");
        body.CoachCount.Should().Be(0);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var plan = await db.SubscriptionPlans
            .AsNoTracking()
            .Include(p => p.Prices)
            .FirstAsync(p => p.Code == code, TestContext.Current.CancellationToken);

        var price = plan.Prices.Should().ContainSingle().Subject;
        price.ValidTo.Should().BeNull();
        price.PriceMinorUnits.Should().Be(plan.PriceMinorUnits);
        price.SecondRoleAddOnPriceMinorUnits.Should().Be(14900);
        price.ExternalPriceId.Should().Be("price_create");
    }

    [Fact]
    public async Task Create_WithBothRolesAndAddOnPrice_Returns400()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);

        var response = await client.PostAsJsonAsync(
            "/admin/subscription-plans",
            ValidPayload(UniqueCode()) with { SecondRoleAddOnPriceMinorUnits = 14900 },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_WithoutCanUsePayments_DefaultsToFalse()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = UniqueCode();

        var response = await client.PostAsJsonAsync(
            "/admin/subscription-plans",
            new
            {
                Code = code,
                NameCs = "Malý",
                NameEn = "Small",
                NameDe = "Klein",
                BothRolesIncluded = true,
                Currency = "CZK",
                BillingInterval = "Monthly",
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.SubscriptionPlans
            .AsNoTracking()
            .FirstAsync(p => p.Code == code, TestContext.Current.CancellationToken);

        persisted.CanUsePayments.Should().BeFalse();
    }

    [Fact]
    public async Task Create_DuplicateCode_Returns409()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = UniqueCode();

        var first = await client.PostAsJsonAsync(
            "/admin/subscription-plans", ValidPayload(code), TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await client.PostAsJsonAsync(
            "/admin/subscription-plans", ValidPayload(code), TestContext.Current.CancellationToken);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Create_ConcurrentDuplicateCode_NeverReturns500()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var payload = ValidPayload(UniqueCode());

        var firstCall = client.PostAsJsonAsync(
            "/admin/subscription-plans", payload, TestContext.Current.CancellationToken);
        var secondCall = client.PostAsJsonAsync(
            "/admin/subscription-plans", payload, TestContext.Current.CancellationToken);

        var responses = await Task.WhenAll(firstCall, secondCall);

        // Whichever request loses the race — caught by the AnyAsync pre-check or by the
        // unique-constraint catch on SaveChangesAsync — must surface as 409, never 500.
        responses.Should().OnlyContain(r =>
            r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.Conflict);
        responses.Should().ContainSingle(r => r.StatusCode == HttpStatusCode.Created);
        responses.Should().ContainSingle(r => r.StatusCode == HttpStatusCode.Conflict);
    }

    private sealed record CreatePlanPayload(
        string Code,
        string NameCs,
        string NameEn,
        string NameDe,
        bool BothRolesIncluded,
        long? SecondRoleAddOnPriceMinorUnits,
        bool CanCreatePlans,
        bool CanMessage,
        bool CanSendQuestionnaires,
        bool CanUseWeeklyCheckIns,
        bool CanUsePerClientCheckInConfig,
        bool CanUsePayments,
        bool CanUsePlanTemplates,
        bool CanUseRecipes,
        bool CanUseIngredients,
        bool CanUseProgressPhotos,
        bool CanUseWeightAndMeasurements,
        bool CanUseAutomations,
        bool CanUsePackages,
        int? MaxActiveClients,
        long PriceMinorUnits,
        string Currency,
        string BillingInterval,
        string? ExternalPriceId,
        bool IsActive);
}
