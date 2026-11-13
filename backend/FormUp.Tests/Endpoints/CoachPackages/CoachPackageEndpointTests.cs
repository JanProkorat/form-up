using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.CoachPackages.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.ClientPayments;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.CoachPackages;

/// <summary>
/// Integration tests for the coach package endpoints under /payments/packages.
/// </summary>
[Collection(TestCollection.Name)]
public class CoachPackageEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static object ValidBody(
        decimal price = 1500m, string name = "Online coaching", string currency = "CZK", string? description = null) => new
    {
        Name = name,
        Description = description,
        Currency = currency,
        Price = price,
        Billing = "Monthly",
        ShowOnProfile = true
    };

    private async Task<CoachPackageDto> CreateAsync(Actor coach, object? body = null)
    {
        var response = await coach.Http.PostAsJsonAsync(
            "/payments/packages", body ?? ValidBody(), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<CoachPackageDto>(WireOptions, TestContext.Current.CancellationToken))!;
    }

    private Task<HttpResponseMessage> UpdateAsync(Actor coach, Guid packageId, object body) =>
        coach.Http.PutAsJsonAsync($"/payments/packages/{packageId}", body, TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> ArchiveAsync(Actor coach, Guid packageId) =>
        coach.Http.PostAsync($"/payments/packages/{packageId}/archive", JsonContent.Create(new { }), TestContext.Current.CancellationToken);

    private async Task<CoachPackageDto> GetAsync(Actor coach, Guid packageId) =>
        (await coach.Http.GetFromJsonAsync<CoachPackageDto>(
            $"/payments/packages/{packageId}", WireOptions, TestContext.Current.CancellationToken))!;

    private async Task<List<decimal>> ReadPriceHistoryAsync(Guid packageId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.CoachPackagePrices.AsNoTracking()
            .Where(p => p.CoachPackage.PublicId == packageId)
            .OrderBy(p => p.EffectiveFromUtc).ThenBy(p => p.Id)
            .Select(p => p.Price)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<(string? Old, string? New)>> ReadAuditAsync(Guid packageId, string action)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var rows = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == packageId && a.Action == action)
            .ToListAsync(TestContext.Current.CancellationToken);

        return rows.Select(a => (a.OldValues, a.NewValues)).ToList();
    }

    [Fact]
    public async Task CreateThenUpdate_PriceHistory_GetsARowOnlyWhenThePriceChangesAndAuditsOldAndNew()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);

        var package = await CreateAsync(coach, ValidBody(currency: "EUR", description: "  Weekly plan + chat  "));

        package.Price.Should().Be(1500m);
        package.Currency.Should().Be(PaymentCurrency.EUR);
        package.Description.Should().Be("Weekly plan + chat");
        package.Billing.Should().Be(PackageBillingInterval.Monthly);
        (await ReadPriceHistoryAsync(package.PackageId)).Should().Equal(1500m);
        (await ReadAuditAsync(package.PackageId, "CreateCoachPackage")).Should().ContainSingle()
            .Which.New.Should().Contain("1500");

        (await UpdateAsync(coach, package.PackageId, ValidBody(name: "Renamed", currency: "EUR"))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ReadPriceHistoryAsync(package.PackageId)).Should().Equal(1500m);
        (await ReadAuditAsync(package.PackageId, "ChangeCoachPackagePrice")).Should().BeEmpty();

        (await UpdateAsync(coach, package.PackageId, ValidBody(price: 1800m, currency: "EUR"))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ReadPriceHistoryAsync(package.PackageId)).Should().Equal(1500m, 1800m);
        var audit = (await ReadAuditAsync(package.PackageId, "ChangeCoachPackagePrice")).Should().ContainSingle().Subject;
        audit.Old.Should().Contain("1500");
        audit.New.Should().Contain("1800");
    }

    [Fact]
    public async Task Create_InvalidBody_Returns400()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);

        var response = await coach.Http.PostAsJsonAsync(
            "/payments/packages", ValidBody(price: 0m), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Archive_IsIdempotentAndAnArchivedPackageRefusesUpdatesWith409()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var package = await CreateAsync(coach);

        var first = await ArchiveAsync(coach, package.PackageId);
        var archivedAt = (await GetAsync(coach, package.PackageId)).ArchivedAt;
        var second = await ArchiveAsync(coach, package.PackageId);

        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        second.StatusCode.Should().Be(HttpStatusCode.NoContent);
        archivedAt.Should().NotBeNull();
        (await GetAsync(coach, package.PackageId)).ArchivedAt.Should().Be(archivedAt);

        var update = await UpdateAsync(coach, package.PackageId, ValidBody(price: 2000m));

        update.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(update)).Should().Be("PACKAGE_ARCHIVED");
    }

    [Fact]
    public async Task ForeignOrUnknownPackage_Returns404OnGetUpdateAndArchive()
    {
        var owner = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var intruder = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var package = await CreateAsync(owner);

        foreach (var packageId in new[] { package.PackageId, Guid.NewGuid() })
        {
            (await intruder.Http.GetAsync($"/payments/packages/{packageId}", TestContext.Current.CancellationToken))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await UpdateAsync(intruder, packageId, ValidBody())).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ArchiveAsync(intruder, packageId)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        var untouched = await GetAsync(owner, package.PackageId);
        untouched.ArchivedAt.Should().BeNull();
        untouched.Price.Should().Be(1500m);
    }

    [Fact]
    public async Task List_ReturnsOnlyOwnActivePackages_ArchivedOnRequest()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var other = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var active = await CreateAsync(coach, ValidBody(name: "Active"));
        var archived = await CreateAsync(coach, ValidBody(name: "Archived"));
        await CreateAsync(other);
        await ArchiveAsync(coach, archived.PackageId);

        var activeOnly = await coach.Http.GetFromJsonAsync<ListBody>("/payments/packages", WireOptions, TestContext.Current.CancellationToken);
        var everything = await coach.Http.GetFromJsonAsync<ListBody>("/payments/packages?includeArchived=true", WireOptions, TestContext.Current.CancellationToken);

        activeOnly!.Items.Select(p => p.PackageId).Should().Equal(active.PackageId);
        everything!.Items.Select(p => p.PackageId).Should().Equal(active.PackageId, archived.PackageId);
    }

    [Fact]
    public async Task ListAndGet_ClientWithTerms_CountsTheClientOnThePackage()
    {
        var world = await ClientPaymentFixtures.CreateWorldAsync(factory);
        var used = await ClientPaymentFixtures.CreatePackageAsync(world.Coach, name: "Used");
        var unused = await ClientPaymentFixtures.CreatePackageAsync(world.Coach, name: "Unused");
        await ClientPaymentFixtures.SetTermsOkAsync(world, used);

        var list = await world.Coach.Http.GetFromJsonAsync<ListBody>("/payments/packages", WireOptions, TestContext.Current.CancellationToken);

        list!.Items.Single(p => p.PackageId == used).ClientCount.Should().Be(1);
        list.Items.Single(p => p.PackageId == unused).ClientCount.Should().Be(0);
        (await GetAsync(world.Coach, used)).ClientCount.Should().Be(1);
    }

    [Fact]
    public async Task Update_CurrencyWhileClientTermsUseThePackage_Returns409CurrencyLocked()
    {
        var world = await ClientPaymentFixtures.CreateWorldAsync(factory);
        var packageId = await ClientPaymentFixtures.CreatePackageAsync(world.Coach);
        await ClientPaymentFixtures.SetTermsOkAsync(world, packageId);

        var response = await UpdateAsync(world.Coach, packageId, ValidBody(price: 60m, currency: "EUR"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(response)).Should().Be("CURRENCY_LOCKED");
        (await GetAsync(world.Coach, packageId)).Currency.Should().Be(PaymentCurrency.CZK);

        // Once the client moves to another package, the old one is no longer in use and may change currency.
        var other = await ClientPaymentFixtures.CreatePackageAsync(world.Coach, name: "Other");
        await ClientPaymentFixtures.SetTermsOkAsync(world, other);

        (await UpdateAsync(world.Coach, packageId, ValidBody(price: 60m, currency: "EUR"))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record ListBody(List<CoachPackageDto> Items);
}
