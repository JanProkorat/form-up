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
using FormUp.Tests.Endpoints.Billing;
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
public class UpdateSubscriptionPlanEndpointTests(FitnessApiFactory factory) : IDisposable
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
        CanUsePlanTemplates: true,
        CanUseRecipes: true,
        CanUseIngredients: true,
        CanUseProgressPhotos: true,
        CanUseWeightAndMeasurements: true,
        CanUseAutomations: true,
        CanUsePackages: true,
        MaxActiveClients: 20,
        PriceMinorUnits: 50000,
        Currency: "EUR",
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
        persisted.CanUsePayments.Should().BeTrue();
        persisted.CanUsePackages.Should().BeTrue();
        persisted.CanUseAutomations.Should().BeTrue();
        persisted.BothRolesIncluded.Should().BeTrue();
        persisted.SecondRoleAddOnPriceMinorUnits.Should().BeNull();
    }

    [Fact]
    public async Task Update_PerClientCheckInsWithoutWeeklyCheckIns_Returns400()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync();

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}",
            UpdatedPayload(code) with { CanUsePerClientCheckInConfig = true, CanUseWeeklyCheckIns = false },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
        prices[1].ExternalPriceId.Should().StartWith("price_fake_", "the typed ExternalPriceId is ignored; the backend creates the price");

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
    public async Task Update_PriceUnchangedAndRowHasNoStripePrice_KeepsOneRowAndBackfillsThePriceFromStripe()
    {
        BillingTestData.ResetProvider(factory);
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
        only.ExternalPriceId.Should().StartWith("price_fake_", "the typed ExternalPriceId is ignored");
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
    public async Task Update_PriceChange_CreatesTheStripePriceForTheNewRow()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync();

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}", UpdatedPayload(code), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Prices.Should().ContainSingle(call => call.PlanCode == code)
            .Which.Should().Be(new FakeSubscriptionService.PriceCall(code, 50000, "EUR", BillingInterval.Monthly));
    }

    [Fact]
    public async Task Update_WhenStripeIsNotConfigured_SavesLocallyWithoutAStripePrice()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.IsConfigured = false;
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync();

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}", UpdatedPayload(code), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Prices.Should().BeEmpty();
        (await ReadPricesAsync(code)).Single(p => p.ValidTo is null).ExternalPriceId.Should().BeNull();
    }

    [Fact]
    public async Task Update_WhenStripeFailsCreatingThePrice_Returns502_AndSavesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.Fail = true;
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync();

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}", UpdatedPayload(code), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await ReadPricesAsync(code)).Should().ContainSingle().Which.ValidTo.Should().BeNull();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.SubscriptionPlans.AsNoTracking().FirstAsync(p => p.Code == code, TestContext.Current.CancellationToken))
            .NameEn.Should().Be("Original");
    }

    [Fact]
    public async Task Update_MoveFlag_SchedulesTheRenewalAtStripeOnlyForCoachesWithAStripeSubscription()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync();
        var withStripe = await TestActors.Trainer(factory).CreateAsync();
        var withoutStripe = await TestActors.Trainer(factory).CreateAsync();
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, withStripe, code);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, withoutStripe, code);
        var subscriptionId = $"sub_{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CoachSubscriptions.Where(cs => cs.ProfessionalProfileId == withStripe.ProfileId)
                .ExecuteUpdateAsync(s => s.SetProperty(cs => cs.ExternalSubscriptionId, subscriptionId), TestContext.Current.CancellationToken);
        }

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}",
            UpdatedPayload(code) with { MoveExistingCoachesAtNextRenewal = true },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var change = fake.PlanChanges.Should().ContainSingle().Subject;
        change.SubscriptionId.Should().Be(subscriptionId);
        change.AtNextRenewal.Should().BeTrue();
        change.PlanPriceExternalId.Should().StartWith("price_fake_");
        (await ReadSubscriptionAsync(withoutStripe.ProfileId)).PendingSubscriptionPlanPriceId.Should().NotBeNull();
    }

    [Fact]
    public async Task Update_MoveFlagWhenStripeFailsScheduling_Returns502_AndMarksNoCoach()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync();
        var coach = await TestActors.Trainer(factory).CreateAsync();
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, code);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CoachSubscriptions.Where(cs => cs.ProfessionalProfileId == coach.ProfileId)
                .ExecuteUpdateAsync(s => s.SetProperty(cs => cs.ExternalSubscriptionId, "sub_fail"), TestContext.Current.CancellationToken);
        }

        // The price is created first and succeeds; the schedule call then fails.
        fake.FailPlanChanges = true;

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}",
            UpdatedPayload(code) with { MoveExistingCoachesAtNextRenewal = true },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await ReadSubscriptionAsync(coach.ProfileId)).PendingSubscriptionPlanPriceId.Should().BeNull();
        (await ReadPricesAsync(code)).Should().ContainSingle();
    }

    private async Task<string> SeedPlanWithAnnualRowAsync()
    {
        var code = await SeedPlanAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var planId = await db.SubscriptionPlans.Where(p => p.Code == code).Select(p => p.Id)
            .SingleAsync(TestContext.Current.CancellationToken);
        db.SubscriptionPlanPrices.Add(new SubscriptionPlanPrice
        {
            SubscriptionPlanId = planId,
            BillingInterval = BillingInterval.Annual,
            PriceMinorUnits = 100000,
            SecondRoleAddOnPriceMinorUnits = 50000,
            Currency = "CZK",
            ExternalPriceId = "price_annual_seed",
            ValidFrom = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return code;
    }

    /// <summary>The seeded plan's own prices, so the payload changes nothing unless a test overrides a field.</summary>
    private static UpdatePlanPayload UnchangedPayload(string code) => UpdatedPayload(code) with
    {
        BothRolesIncluded = false,
        SecondRoleAddOnPriceMinorUnits = 5000,
        PriceMinorUnits = 10000,
        Currency = "CZK",
        AnnualPriceMinorUnits = 100000,
        AnnualSecondRoleAddOnPriceMinorUnits = 50000,
    };

    [Fact]
    public async Task Update_OnlyMonthlyPriceChanges_OpensOneMonthlyRowAndLeavesTheAnnualRow()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanWithAnnualRowAsync();

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}",
            UnchangedPayload(code) with { PriceMinorUnits = 12000 },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var prices = await ReadPricesAsync(code);
        prices.Count(p => p.BillingInterval == BillingInterval.Monthly).Should().Be(2);
        prices.Count(p => p.BillingInterval == BillingInterval.Monthly && p.ValidTo is null).Should().Be(1);
        var annual = prices.Should().ContainSingle(p => p.BillingInterval == BillingInterval.Annual).Subject;
        annual.ValidTo.Should().BeNull();
        annual.ExternalPriceId.Should().Be("price_annual_seed");
        fake.Prices.Should().ContainSingle(call => call.PlanCode == code)
            .Which.Interval.Should().Be(BillingInterval.Monthly);
    }

    [Fact]
    public async Task Update_AnnualPriceUnchanged_OpensNoAnnualRowAndCreatesNoAnnualStripePrice()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanWithAnnualRowAsync();

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}", UnchangedPayload(code), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var prices = await ReadPricesAsync(code);
        prices.Count(p => p.BillingInterval == BillingInterval.Annual).Should().Be(1);
        prices.Single(p => p.BillingInterval == BillingInterval.Annual).ExternalPriceId.Should().Be("price_annual_seed");
        fake.Prices.Should().NotContain(call => call.Interval == BillingInterval.Annual);
    }

    [Fact]
    public async Task Update_AnnualPriceChangedWithMoveFlag_MovesOnlyAnnualCoachesToTheNewAnnualRow()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanWithAnnualRowAsync();
        var annualCoach = await TestActors.Trainer(factory).CreateAsync();
        var monthlyCoach = await TestActors.Trainer(factory).CreateAsync();
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory, annualCoach, code, interval: BillingInterval.Annual);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, monthlyCoach, code);
        var oldAnnualId = (await ReadPricesAsync(code)).Single(p => p.BillingInterval == BillingInterval.Annual).Id;

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}",
            UnchangedPayload(code) with { AnnualPriceMinorUnits = 120000, MoveExistingCoachesAtNextRenewal = true },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var newAnnualId = (await ReadPricesAsync(code))
            .Single(p => p.BillingInterval == BillingInterval.Annual && p.ValidTo is null).Id;
        newAnnualId.Should().NotBe(oldAnnualId);

        var annual = await ReadSubscriptionAsync(annualCoach.ProfileId);
        annual.SubscriptionPlanPriceId.Should().Be(oldAnnualId, "the coach keeps paying the old price until renewal");
        annual.PendingSubscriptionPlanPriceId.Should().Be(newAnnualId);

        var monthly = await ReadSubscriptionAsync(monthlyCoach.ProfileId);
        monthly.PendingSubscriptionPlanPriceId.Should().BeNull("a monthly coach is not moved onto the annual price");
    }

    [Fact]
    public async Task Update_AnnualPriceOmitted_ClosesTheAnnualRowAndTheCoachOnItKeepsIt()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanWithAnnualRowAsync();
        var coach = await TestActors.Trainer(factory).CreateAsync();
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, code, interval: BillingInterval.Annual);
        var annualId = (await ReadPricesAsync(code)).Single(p => p.BillingInterval == BillingInterval.Annual).Id;

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}",
            UnchangedPayload(code) with { AnnualPriceMinorUnits = null, AnnualSecondRoleAddOnPriceMinorUnits = null },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionPlanDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.AnnualPriceMinorUnits.Should().BeNull();

        var annual = (await ReadPricesAsync(code)).Single(p => p.BillingInterval == BillingInterval.Annual);
        annual.Id.Should().Be(annualId);
        annual.ValidTo.Should().NotBeNull();
        (await ReadSubscriptionAsync(coach.ProfileId)).SubscriptionPlanPriceId.Should().Be(annualId);
        fake.Prices.Should().NotContain(call => call.Interval == BillingInterval.Annual);
    }

    [Fact]
    public async Task Update_StripeFailsCreatingTheAnnualPrice_Returns502_AndTheOpenAnnualRowStaysOpen()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.FailAnnualPriceCreation = true;
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanWithAnnualRowAsync();

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}",
            UnchangedPayload(code) with { AnnualPriceMinorUnits = 120000 },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(ErrorCodes.BillingProviderError);

        var prices = await ReadPricesAsync(code);
        prices.Should().HaveCount(2);
        prices.Should().OnlyContain(p => p.ValidTo == null);
        prices.Single(p => p.BillingInterval == BillingInterval.Annual).PriceMinorUnits.Should().Be(100000);
    }

    [Fact]
    public async Task Update_NewAnnualPrice_CreatesAYearStripePriceAndExposesItInTheDto()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync();

        var response = await client.PutAsJsonAsync(
            $"/admin/subscription-plans/{code}",
            UnchangedPayload(code),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Prices.Should().Contain(new FakeSubscriptionService.PriceCall(code, 100000, "CZK", BillingInterval.Annual));

        var body = await response.Content.ReadFromJsonAsync<SubscriptionPlanDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.AnnualPriceMinorUnits.Should().Be(100000);
        body.AnnualSecondRoleAddOnPriceMinorUnits.Should().Be(50000);
        body.AnnualExternalPriceId.Should().StartWith("price_fake_");
        body.PriceMinorUnits.Should().Be(10000);
    }

    public void Dispose() => BillingTestData.ResetProvider(factory);

    [Fact]
    public async Task Update_FreePlanWithNonZeroPrice_Returns409FreePlanLocked()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);

        var response = await client.PutAsJsonAsync(
            "/admin/subscription-plans/free",
            UpdatedPayload("free") with { PriceMinorUnits = 100, Currency = "CZK" },
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
                CanUsePlanTemplates = true,
                CanUseRecipes = true,
                CanUseIngredients = true,
                CanUseProgressPhotos = true,
                CanUseWeightAndMeasurements = true,
                CanUseAutomations = true,
                CanUsePackages = true,
                // MaxActiveClients intentionally omitted — must 400, not silently grant unlimited.
                PriceMinorUnits = 50000,
                Currency = "EUR",
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
                CanUsePlanTemplates = true,
                CanUseRecipes = true,
                CanUseIngredients = true,
                CanUseProgressPhotos = true,
                CanUseWeightAndMeasurements = true,
                CanUseAutomations = true,
                CanUsePackages = true,
                MaxActiveClients = 20,
                PriceMinorUnits = 50000,
                Currency = "EUR",
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
                CanUsePlanTemplates = true,
                CanUseRecipes = true,
                CanUseIngredients = true,
                CanUseProgressPhotos = true,
                CanUseWeightAndMeasurements = true,
                CanUseAutomations = true,
                CanUsePackages = true,
                MaxActiveClients = 20,
                PriceMinorUnits = 50000,
                Currency = "EUR",
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
                CanUsePlanTemplates = true,
                CanUseRecipes = true,
                CanUseIngredients = true,
                CanUseProgressPhotos = true,
                CanUseWeightAndMeasurements = true,
                CanUseAutomations = true,
                CanUsePackages = true,
                MaxActiveClients = 20,
                PriceMinorUnits = 50000,
                Currency = "EUR",
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
        string? ExternalPriceId,
        bool IsActive,
        bool MoveExistingCoachesAtNextRenewal = false,
        long? AnnualPriceMinorUnits = null,
        long? AnnualSecondRoleAddOnPriceMinorUnits = null);
}
