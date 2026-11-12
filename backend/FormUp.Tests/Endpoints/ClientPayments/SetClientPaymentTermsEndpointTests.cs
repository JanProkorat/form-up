using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.ClientPayments.SetClientPaymentTerms;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FormUp.Tests.Endpoints.ClientPayments.ClientPaymentFixtures;
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.ClientPayments;

/// <summary>
/// Integration tests for PUT /payments/clients/{ClientId}/terms.
/// </summary>
[Collection(TestCollection.Name)]
public class SetClientPaymentTermsEndpointTests(FitnessApiFactory factory)
{
    [Fact]
    public async Task SetTerms_FirstTime_CreatesThisAndNextPeriodNumbersTheClientAndIsIdempotent()
    {
        // A link granting no plan domain is enough: payments belong to the relationship.
        var world = await CreateWorldAsync(factory, linkGrantsPlans: false);
        var packageId = await CreatePackageAsync(world.Coach, 2500m);

        var response = await SetTermsAsync(world, packageId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadAsync<SetClientPaymentTermsResponse>(response);
        body.PackageId.Should().Be(packageId);
        body.Amount.Should().Be(2500m);
        body.AppliesFromPeriodStart.Should().Be(Today);

        var rows = (await ListAsync(world)).OrderBy(p => p.PeriodStart).ToList();
        rows.Should().HaveCount(2);
        rows[0].PeriodStart.Should().Be(Today);
        rows[0].Status.Should().Be(ClientPaymentStatus.Upcoming);
        rows[0].Amount.Should().Be(2500m);
        rows[0].VariableSymbol.Should().Be($"{Today.Year}{Today.Month:D2}0001");
        rows[1].PeriodStart.Should().BeAfter(Today);

        await SetTermsOkAsync(world, packageId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.ClientPaymentTerms.CountAsync(
                t => t.ClientPaymentAssignment.ClientProfileId == world.Client.ProfileId,
                TestContext.Current.CancellationToken)).Should().Be(1, "the same terms again change nothing");
        }

        (await ListAsync(world)).Select(p => p.PaymentId).Should().BeEquivalentTo(rows.Select(p => p.PaymentId));

        var secondClient = world with { Client = await AddClientAsync(world) };
        await SetTermsOkAsync(secondClient, packageId);

        (await ListAsync(secondClient)).Select(p => p.VariableSymbol).Should().OnlyContain(symbol => symbol!.EndsWith("0002"));
    }

    [Fact]
    public async Task SetTerms_PackageThenPriceChange_KeepsTheStartedPeriodAndRepricesTheNext()
    {
        var world = await CreateWorldAsync(factory);
        var original = await CreatePackageAsync(world.Coach, 2500m, "Online coaching");
        var cheaper = await CreatePackageAsync(world.Coach, 1000m, "Light");
        await SetTermsOkAsync(world, original);

        await SetTermsOkAsync(world, cheaper);

        var afterPackage = (await ListAsync(world)).OrderBy(p => p.PeriodStart).ToList();
        afterPackage[0].Amount.Should().Be(2500m, "the current period had already started when the terms changed");
        afterPackage[0].PackageName.Should().Be("Online coaching");
        afterPackage[1].Amount.Should().Be(1000m);
        afterPackage[1].PackageName.Should().Be("Light");

        await SetTermsOkAsync(world, cheaper, customPrice: 800m);

        var afterPrice = (await ListAsync(world)).OrderBy(p => p.PeriodStart).ToList();
        afterPrice.Select(p => p.PaymentId).Should().Equal(afterPackage.Select(p => p.PaymentId), "a price-only change keeps the period rows");
        afterPrice[0].Amount.Should().Be(2500m);
        afterPrice[1].Amount.Should().Be(800m);
    }

    [Fact]
    public async Task SetTerms_MonthlyToOneOff_ReplacesTheStaleFutureRowAndAuditsTheRemoval()
    {
        var world = await CreateWorldAsync(factory);
        var monthly = await CreatePackageAsync(world.Coach, 2500m);
        var oneOff = await CreatePackageAsync(world.Coach, 900m, "Workshop", "OneOff");
        await SetTermsOkAsync(world, monthly);
        var before = (await ListAsync(world)).OrderBy(p => p.PeriodStart).ToList();

        await SetTermsOkAsync(world, oneOff);

        var after = (await ListAsync(world)).OrderBy(p => p.PeriodStart).ToList();
        after.Should().HaveCount(2);
        after[0].PaymentId.Should().Be(before[0].PaymentId, "the current period had started");
        after[1].PaymentId.Should().NotBe(before[1].PaymentId);
        after[1].PeriodEnd.Should().Be(after[1].PeriodStart, "a one-off package bills a single day");
        after[1].PackageName.Should().Be("Workshop");
        after[1].Amount.Should().Be(900m);

        var audit = (await ReadAuditAsync(world)).Last(a => a.Action == "SetClientPaymentTerms");
        audit.NewValues.Should().Contain("\"removed\":1");
    }

    [Fact]
    public async Task SetTerms_RequestedRowFromTheOldTerms_StaysUntouched()
    {
        var world = await CreateWorldAsync(factory);
        var monthly = await CreatePackageAsync(world.Coach, 2500m);
        var cheaper = await CreatePackageAsync(world.Coach, 100m, "Light");
        await SetTermsOkAsync(world, monthly);
        var future = (await ListAsync(world)).OrderBy(p => p.PeriodStart).Last();
        (await PostActionAsync(world, future.PaymentId, "request", new { SendEmail = true })).StatusCode.Should().Be(HttpStatusCode.OK);
        var locked = await ReadStoredAsync(factory, future.PaymentId);

        await SetTermsOkAsync(world, cheaper);

        var kept = await ReadStoredAsync(factory, future.PaymentId);
        kept.Status.Should().Be(ClientPaymentStatus.Requested);
        kept.Amount.Should().Be(2500m);
        kept.VariableSymbol.Should().Be(locked.VariableSymbol);
        kept.AccountIban.Should().Be(ValidIban);
        kept.PackageName.Should().Be("Online coaching");
    }

    [Fact]
    public async Task SetTerms_DiscountOrCustomPrice_PricesTheRows()
    {
        var world = await CreateWorldAsync(factory);
        var packageId = await CreatePackageAsync(world.Coach, 2500m);
        var discountId = await CreateDiscountAsync(world.Coach, 30m);
        var customClient = world with { Client = await AddClientAsync(world) };

        await SetTermsOkAsync(world, packageId, discountId);
        await SetTermsOkAsync(customClient, packageId, customPrice: 1200m);

        var discounted = await ListAsync(world);
        discounted.Should().OnlyContain(p => p.Amount == 1750m && p.BasePrice == 2500m);
        discounted[0].DiscountLabel.Should().Contain("Friends & family").And.Contain("30");
        (await ListAsync(customClient)).Should().OnlyContain(p => p.Amount == 1200m, "a custom price replaces the package price");
    }

    [Fact]
    public async Task SetTerms_FirstPeriodStartInTheFuture_CreatesOnlyThatPeriod()
    {
        var world = await CreateWorldAsync(factory);
        var packageId = await CreatePackageAsync(world.Coach);
        var start = Today.AddDays(40);

        await SetTermsOkAsync(world, packageId, firstPeriodStart: start);

        var rows = await ListAsync(world);
        rows.Should().ContainSingle().Which.PeriodStart.Should().Be(start);
    }

    [Fact]
    public async Task SetTerms_DiscountAndCustomPrice_Returns400()
    {
        var world = await CreateWorldAsync(factory);
        var packageId = await CreatePackageAsync(world.Coach);
        var discountId = await CreateDiscountAsync(world.Coach);

        var response = await SetTermsAsync(world, packageId, discountId, customPrice: 1000m);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodesAsync(response)).Should().Contain("PAYMENT_TERMS_DISCOUNT_AND_CUSTOM_PRICE");
    }

    [Fact]
    public async Task SetTerms_PackageOrDiscountOfAnotherCoach_Returns404()
    {
        var world = await CreateWorldAsync(factory);
        var otherCoach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var packageId = await CreatePackageAsync(world.Coach);
        var foreignPackage = await CreatePackageAsync(otherCoach);
        var foreignDiscount = await CreateDiscountAsync(otherCoach);

        (await SetTermsAsync(world, foreignPackage)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SetTermsAsync(world, packageId, foreignDiscount)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ListAsync(world)).Should().BeEmpty();
    }

    [Fact]
    public async Task SetTerms_ArchivedPackageArchivedDiscountOrDiscountNotCoveringThePackage_Returns409()
    {
        var world = await CreateWorldAsync(factory);
        var packageId = await CreatePackageAsync(world.Coach, name: "Online coaching");
        var archivedPackage = await CreatePackageAsync(world.Coach, name: "Old");
        var otherPackage = await CreatePackageAsync(world.Coach, name: "Nutrition");
        var archivedDiscount = await CreateDiscountAsync(world.Coach);
        var narrowDiscount = await CreateDiscountAsync(world.Coach, packageIds: [otherPackage]);
        await world.Coach.Http.PostAsync($"/payments/packages/{archivedPackage}/archive", Empty(), TestContext.Current.CancellationToken);
        await world.Coach.Http.PostAsync($"/payments/discounts/{archivedDiscount}/archive", Empty(), TestContext.Current.CancellationToken);

        var cases = new (HttpResponseMessage Response, string Code)[]
        {
            (await SetTermsAsync(world, archivedPackage), "PACKAGE_ARCHIVED"),
            (await SetTermsAsync(world, packageId, archivedDiscount), "DISCOUNT_ARCHIVED"),
            (await SetTermsAsync(world, packageId, narrowDiscount), "DISCOUNT_PACKAGE_INVALID")
        };

        foreach (var (response, code) in cases)
        {
            response.StatusCode.Should().Be(HttpStatusCode.Conflict, code);
            (await ProblemCodeAsync(response)).Should().Be(code);
        }
    }

    [Fact]
    public async Task SetTerms_FixedDiscountInAnotherCurrencyThanThePackage_Returns400CurrencyMismatch()
    {
        var world = await CreateWorldAsync(factory);
        var euroPackage = await CreatePackageAsync(world.Coach, 100m, currency: "EUR");
        var crownDiscount = await CreateDiscountAsync(world.Coach, 20m, type: "FixedAmount", currency: "CZK");

        var response = await SetTermsAsync(world, euroPackage, crownDiscount);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(response)).Should().Be("CURRENCY_MISMATCH");
    }

    [Fact]
    public async Task SetTerms_CustomPriceAboveTheEuroCap_Returns400AmountInvalid()
    {
        var world = await CreateWorldAsync(factory);
        var euroPackage = await CreatePackageAsync(world.Coach, 100m, currency: "EUR");

        var response = await SetTermsAsync(world, euroPackage, customPrice: 50_000.01m);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodesAsync(response)).Should().Contain("PAYMENT_AMOUNT_INVALID");
    }

    [Fact]
    public async Task SetTerms_CoachHasNoClientNumberLeft_Returns409AndStoresNothing()
    {
        var world = await CreateWorldAsync(factory);
        var packageId = await CreatePackageAsync(world.Coach);
        var other = await AddClientAsync(world);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ClientPaymentAssignments.Add(new ClientPaymentAssignment
            {
                ProfessionalProfileId = world.Coach.ProfileId,
                ClientProfileId = other.ProfileId,
                ClientNumber = ClientPaymentSchedule.MaxClientNumber
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await SetTermsAsync(world, packageId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(response)).Should().Be("PAYMENT_CLIENT_NUMBER_EXHAUSTED");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.ClientPaymentTerms.AnyAsync(
                t => t.ClientPaymentAssignment.ClientProfileId == world.Client.ProfileId,
                TestContext.Current.CancellationToken)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task SetTerms_EuroPackage_GivesEuroPaymentRows()
    {
        var world = await CreateWorldAsync(factory);
        var euroPackage = await CreatePackageAsync(world.Coach, 100m, currency: "EUR");

        await SetTermsOkAsync(world, euroPackage);

        (await ListAsync(world)).Should().OnlyContain(p => p.Currency == PaymentCurrency.EUR);
    }

    [Fact]
    public async Task SetTerms_MonthlyPackagesDiscount_CoversMonthlyPackagesOnly()
    {
        var world = await CreateWorldAsync(factory);
        var monthly = await CreatePackageAsync(world.Coach, 2000m);
        var oneOff = await CreatePackageAsync(world.Coach, 2000m, name: "Camp", billing: "OneOff");
        var discount = await world.Coach.Http.PostAsJsonAsync(
            "/payments/discounts",
            new { Name = "Monthly only", Type = "Percentage", Value = 50m, Duration = "EveryPayment", Scope = "MonthlyPackages", PackageIds = new List<Guid>() },
            TestContext.Current.CancellationToken);
        var discountId = (await ReadAsync<FormUp.Application.Features.Discounts.Shared.CoachDiscountDto>(discount)).DiscountId;

        await SetTermsOkAsync(world, monthly, discountId);

        (await ListAsync(world)).Should().OnlyContain(p => p.Amount == 1000m);
        (await SetTermsAsync(world, oneOff, discountId)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SetTerms_ConcurrentReadsOfTheNextPeriod_CreateExactlyOneRow()
    {
        var world = await CreateWorldAsync(factory);
        var packageId = await CreatePackageAsync(world.Coach);
        await SetTermsOkAsync(world, packageId, firstPeriodStart: Today);

        // Both rows exist already; delete the future one so every reader has to recreate it at once.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.ClientPayments
                .Where(p => p.ClientPaymentAssignment.ClientProfileId == world.Client.ProfileId && p.PeriodStart > Today)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            world.Coach.Http.GetAsync($"{world.Route}/payments", TestContext.Current.CancellationToken)));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        (await ListAsync(world)).Should().HaveCount(2);
    }
}
