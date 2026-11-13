using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.SubscriptionPlans.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.SubscriptionPlans;

/// <summary>
/// Integration tests for <c>PUT /admin/subscription-plans/{Code}</c>
/// (<see cref="Application.Features.SubscriptionPlans.UpdateSubscriptionPlan.UpdateSubscriptionPlanEndpoint"/>).
/// Uses <see cref="FitnessApiFactory"/> since Admin is not publicly self-registerable — the
/// only way to prove the role gate is the real ASP.NET Core pipeline.
/// </summary>
[Collection(TestCollection.Name)]
public class UpdateSubscriptionPlanEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string UniqueCode() => $"tier-{Guid.NewGuid():N}"[..30];

    private async Task<string> SeedPlanAsync()
    {
        var code = UniqueCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var plan = new SubscriptionPlan
        {
            Code = code,
            NameCs = "Původní",
            NameEn = "Original",
            NameDe = "Original",
            BothRolesIncluded = false,
            SecondRoleAddOnPriceMinorUnits = 5000,
            Currency = "CZK",
            PriceMinorUnits = 10000,
            BillingInterval = BillingInterval.Monthly,
            MaxActiveClients = 5,
            IsActive = true,
        };
        SubscriptionPlanPriceBuilder.OpenPriceFor(plan);
        db.SubscriptionPlans.Add(plan);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return code;
    }

    private async Task<List<SubscriptionPlanPrice>> ReadPricesAsync(string code)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.SubscriptionPlanPrices
            .AsNoTracking()
            .Where(p => p.SubscriptionPlan.Code == code)
            .OrderBy(p => p.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static UpdatePlanPayload UpdatedPayload(string code) => new(
        Code: code,
        NameCs: "Aktualizováno",
        NameEn: "Updated",
        NameDe: "Aktualisiert",
        BothRolesIncluded: true,
        SecondRoleAddOnPriceMinorUnits: null,
        CanCreatePlans: true,
        CanMessage: true,
        CanSendQuestionnaires: true,
        CanUseWeeklyCheckIns: true,
        CanUsePerClientCheckInConfig: true,
        CanUsePayments: true,
        MaxActiveClients: 20,
        PriceMinorUnits: 50000,
        Currency: "EUR",
        BillingInterval: "Annual",
        ExternalPriceId: "price_123",
        IsActive: true);

    [Fact]
    public async Task Update_ValidRequest_ChangesFields()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync();

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}", UpdatedPayload(code), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionPlanDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.NameEn.Should().Be("Updated");
        body.Currency.Should().Be("EUR");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.SubscriptionPlans
            .AsNoTracking()
            .FirstAsync(p => p.Code == code, TestContext.Current.CancellationToken);

        persisted.NameEn.Should().Be("Updated");
        persisted.PriceMinorUnits.Should().Be(50000);
        persisted.BillingInterval.Should().Be(BillingInterval.Annual);
        persisted.CanUsePayments.Should().BeTrue();
        persisted.BothRolesIncluded.Should().BeTrue();
        persisted.SecondRoleAddOnPriceMinorUnits.Should().BeNull();
    }

    [Fact]
    public async Task Update_PriceChange_ClosesOpenRowOpensNewOneAndPlanMirrorsIt()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync();

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}", UpdatedPayload(code), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var prices = await ReadPricesAsync(code);
        prices.Should().HaveCount(2);
        prices[0].ValidTo.Should().NotBeNull();
        prices[0].PriceMinorUnits.Should().Be(10000);
        prices[1].ValidTo.Should().BeNull();
        prices[1].PriceMinorUnits.Should().Be(50000);
        prices[1].Currency.Should().Be("EUR");
        prices[1].SecondRoleAddOnPriceMinorUnits.Should().BeNull();
        prices[1].ExternalPriceId.Should().Be("price_123");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var plan = await db.SubscriptionPlans.AsNoTracking()
            .FirstAsync(p => p.Code == code, TestContext.Current.CancellationToken);
        plan.PriceMinorUnits.Should().Be(prices[1].PriceMinorUnits);
        plan.Currency.Should().Be(prices[1].Currency);
    }

    [Fact]
    public async Task Update_AddOnPriceChange_OpensNewPriceRow()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync();

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}",
            UpdatedPayload(code) with
            {
                BothRolesIncluded = false,
                SecondRoleAddOnPriceMinorUnits = 7000,
                PriceMinorUnits = 10000,
                Currency = "CZK",
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var prices = await ReadPricesAsync(code);
        prices.Should().HaveCount(2);
        prices.Count(p => p.ValidTo is null).Should().Be(1);
        prices[1].SecondRoleAddOnPriceMinorUnits.Should().Be(7000);
    }

    [Fact]
    public async Task Update_PriceUnchanged_KeepsOneRowAndOnlyUpdatesExternalPriceId()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync();

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}",
            UpdatedPayload(code) with
            {
                BothRolesIncluded = false,
                SecondRoleAddOnPriceMinorUnits = 5000,
                PriceMinorUnits = 10000,
                Currency = "CZK",
                ExternalPriceId = "price_same_row",
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var prices = await ReadPricesAsync(code);
        var only = prices.Should().ContainSingle().Subject;
        only.ValidTo.Should().BeNull();
        only.ExternalPriceId.Should().Be("price_same_row");
    }

    [Fact]
    public async Task Update_PriceChangeWithoutMoveFlag_LeavesCoachesOnTheirRowWithNoPendingMarker()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync();
        var coach = await TestActors.Trainer(factory).CreateAsync();
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, code);
        var originalPriceId = (await ReadPricesAsync(code)).Single().Id;

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}", UpdatedPayload(code), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var subscription = await ReadSubscriptionAsync(coach.ProfileId);
        subscription.SubscriptionPlanPriceId.Should().Be(originalPriceId);
        subscription.PendingSubscriptionPlanPriceId.Should().BeNull();
    }

    [Fact]
    public async Task Update_PriceChangeWithMoveFlag_MarksLiveCoachesToMoveAndKeepsTheirCurrentRow()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync();
        var liveCoach = await TestActors.Trainer(factory).CreateAsync();
        var canceledCoach = await TestActors.Trainer(factory).CreateAsync();
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, liveCoach, code);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, canceledCoach, code, SubscriptionStatus.Canceled);
        var originalPriceId = (await ReadPricesAsync(code)).Single().Id;

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}",
            UpdatedPayload(code) with { MoveExistingCoachesAtNextRenewal = true },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var newPriceId = (await ReadPricesAsync(code)).Single(p => p.ValidTo is null).Id;
        var live = await ReadSubscriptionAsync(liveCoach.ProfileId);
        live.SubscriptionPlanPriceId.Should().Be(originalPriceId, "the coach keeps paying the old price until renewal");
        live.PendingSubscriptionPlanPriceId.Should().Be(newPriceId);

        var canceled = await ReadSubscriptionAsync(canceledCoach.ProfileId);
        canceled.PendingSubscriptionPlanPriceId.Should().BeNull();
    }

    [Fact]
    public async Task Update_FreePlanWithNonZeroPrice_Returns409FreePlanLocked()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);

        var response = await client.PutAsJsonAsync(
            "/admin/subscription-plans/free",
            UpdatedPayload("free") with { PriceMinorUnits = 100, Currency = "CZK", BillingInterval = "Monthly" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(ErrorCodes.FreePlanLocked);
    }

    [Fact]
    public async Task Update_FreePlanDeactivated_Returns409FreePlanLocked()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);

        var response = await client.PutAsJsonAsync(
            "/admin/subscription-plans/free",
            UpdatedPayload("free") with
            {
                PriceMinorUnits = 0,
                Currency = "CZK",
                BillingInterval = "Monthly",
                IsActive = false,
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(ErrorCodes.FreePlanLocked);
    }

    [Fact]
    public async Task Update_BothRolesAndAddOnPriceBothSet_Returns400()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync();

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}",
            UpdatedPayload(code) with { BothRolesIncluded = true, SecondRoleAddOnPriceMinorUnits = 7000 },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<CoachSubscription> ReadSubscriptionAsync(long profileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.CoachSubscriptions
            .AsNoTracking()
            .FirstAsync(cs => cs.ProfessionalProfileId == profileId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Update_BodyIncludesDifferentCode_RouteCodeWinsAndCodeIsUnchanged()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync();
        var attemptedNewCode = UniqueCode();

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}",
            UpdatedPayload(attemptedNewCode),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionPlanDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Code.Should().Be(code);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        (await db.SubscriptionPlans.AsNoTracking().AnyAsync(
            p => p.Code == code, TestContext.Current.CancellationToken)).Should().BeTrue();
        (await db.SubscriptionPlans.AsNoTracking().AnyAsync(
            p => p.Code == attemptedNewCode, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task Update_UnknownCode_Returns404()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var unknownCode = UniqueCode();

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{unknownCode}",
            UpdatedPayload(unknownCode),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_OmittedMaxActiveClients_Returns400()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync();

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}",
            new
            {
                Code = code,
                NameCs = "Aktualizováno",
                NameEn = "Updated",
                NameDe = "Aktualisiert",
                BothRolesIncluded = true,
                CanCreatePlans = true,
                CanMessage = true,
                CanSendQuestionnaires = true,
                CanUseWeeklyCheckIns = true,
                CanUsePerClientCheckInConfig = true,
                CanUsePayments = true,
                // MaxActiveClients intentionally omitted — must 400, not silently grant unlimited.
                PriceMinorUnits = 50000,
                Currency = "EUR",
                BillingInterval = "Annual",
                IsActive = true,
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_OmittedIsActive_Returns400()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync();

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}",
            new
            {
                Code = code,
                NameCs = "Aktualizováno",
                NameEn = "Updated",
                NameDe = "Aktualisiert",
                BothRolesIncluded = true,
                CanCreatePlans = true,
                CanMessage = true,
                CanSendQuestionnaires = true,
                CanUseWeeklyCheckIns = true,
                CanUsePerClientCheckInConfig = true,
                CanUsePayments = true,
                MaxActiveClients = 20,
                PriceMinorUnits = 50000,
                Currency = "EUR",
                BillingInterval = "Annual",
                // IsActive intentionally omitted — must 400, not silently deactivate.
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_OmittedCanFlag_Returns400()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync();

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}",
            new
            {
                Code = code,
                NameCs = "Aktualizováno",
                NameEn = "Updated",
                NameDe = "Aktualisiert",
                BothRolesIncluded = true,
                // CanCreatePlans intentionally omitted — must 400, not silently revoke.
                CanMessage = true,
                CanSendQuestionnaires = true,
                CanUseWeeklyCheckIns = true,
                CanUsePerClientCheckInConfig = true,
                CanUsePayments = true,
                MaxActiveClients = 20,
                PriceMinorUnits = 50000,
                Currency = "EUR",
                BillingInterval = "Annual",
                IsActive = true,
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_OmittedCanUsePayments_Returns400()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync();

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}",
            new
            {
                Code = code,
                NameCs = "Aktualizováno",
                NameEn = "Updated",
                NameDe = "Aktualisiert",
                BothRolesIncluded = true,
                CanCreatePlans = true,
                CanMessage = true,
                CanSendQuestionnaires = true,
                CanUseWeeklyCheckIns = true,
                CanUsePerClientCheckInConfig = true,
                // CanUsePayments intentionally omitted — must 400, not silently revoke.
                MaxActiveClients = 20,
                PriceMinorUnits = 50000,
                Currency = "EUR",
                BillingInterval = "Annual",
                IsActive = true,
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_ExplicitNullMaxActiveClients_MeansUnlimitedAndSucceeds()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync();

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}",
            UpdatedPayload(code) with { MaxActiveClients = null },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.SubscriptionPlans
            .AsNoTracking()
            .FirstAsync(p => p.Code == code, TestContext.Current.CancellationToken);

        persisted.MaxActiveClients.Should().BeNull();
    }

    private sealed record UpdatePlanPayload(
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
        bool IsActive,
        bool MoveExistingCoachesAtNextRenewal = false);
}
