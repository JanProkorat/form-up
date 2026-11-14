using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.SubscriptionPlans.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Endpoints.Billing;
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
public class CreateSubscriptionPlanEndpointTests(FitnessApiFactory factory) : IDisposable
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
                ExternalPriceId = "price_typed_by_admin",
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionPlanDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.SecondRoleAddOnPriceMinorUnits.Should().Be(14900);
        body.BothRolesIncluded.Should().BeFalse();
        body.ExternalPriceId.Should().StartWith("price_fake_", "the typed ExternalPriceId is ignored; the backend creates the price");
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
        price.ExternalPriceId.Should().Be(body.ExternalPriceId);
    }

    [Fact]
    public async Task Create_PaidPlan_CreatesTheStripePriceFromTheRowNotFromTheRequest()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = UniqueCode();

        var response = await client.PostAsJsonAsync(
            "/admin/subscription-plans", ValidPayload(code), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        fake.Prices.Should().ContainSingle(call => call.PlanCode == code)
            .Which.Should().Be(new FakeSubscriptionService.PriceCall(code, 29900, "CZK", BillingInterval.Monthly));
    }

    [Fact]
    public async Task Create_ZeroPrice_NeverCreatesAStripePrice()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = UniqueCode();

        var response = await client.PostAsJsonAsync(
            "/admin/subscription-plans", ValidPayload(code) with { PriceMinorUnits = 0 }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        fake.Prices.Should().BeEmpty();
        (await response.Content.ReadFromJsonAsync<SubscriptionPlanDto>(JsonOptions, TestContext.Current.CancellationToken))!
            .ExternalPriceId.Should().BeNull();
    }

    [Fact]
    public async Task Create_WhenStripeIsNotConfigured_SavesThePlanWithoutAStripePrice()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.IsConfigured = false;
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = UniqueCode();

        var response = await client.PostAsJsonAsync(
            "/admin/subscription-plans", ValidPayload(code), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        fake.Prices.Should().BeEmpty();
        (await response.Content.ReadFromJsonAsync<SubscriptionPlanDto>(JsonOptions, TestContext.Current.CancellationToken))!
            .ExternalPriceId.Should().BeNull();
    }

    [Fact]
    public async Task Create_WhenStripeFails_Returns502_AndSavesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.Fail = true;
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = UniqueCode();

        var response = await client.PostAsJsonAsync(
            "/admin/subscription-plans", ValidPayload(code), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.SubscriptionPlans.AnyAsync(p => p.Code == code, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    public void Dispose() => BillingTestData.ResetProvider(factory);

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
        int? MaxActiveClients,
        long PriceMinorUnits,
        string Currency,
        string BillingInterval,
        string? ExternalPriceId,
        bool IsActive);
}
