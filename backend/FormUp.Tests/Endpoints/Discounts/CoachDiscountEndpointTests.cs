using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Discounts.Shared;
using FormUp.Application.Features.CoachPackages.Shared;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.Discounts;

/// <summary>
/// Integration tests for the coach discount endpoints under /payments/discounts.
/// </summary>
[Collection(TestCollection.Name)]
public class CoachDiscountEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static object DiscountBody(
        IEnumerable<Guid>? packageIds = null,
        bool all = false,
        string name = "Spring sale",
        string type = "Percentage",
        string? currency = null) => new
    {
        Name = name,
        Type = type,
        Value = 10m,
        Currency = currency,
        Duration = "EveryPayment",
        Scope = all ? "AllPackages" : "SelectedPackages",
        PackageIds = packageIds ?? []
    };

    private async Task<Guid> CreatePackageAsync(Actor coach, string currency = "CZK")
    {
        var response = await coach.Http.PostAsJsonAsync(
            "/payments/packages",
            new { Name = "Online coaching", Price = 1500m, Currency = currency, Billing = "Monthly" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<CoachPackageDto>(WireOptions, TestContext.Current.CancellationToken))!.PackageId;
    }

    private async Task<CoachDiscountDto> CreateDiscountAsync(Actor coach, object body)
    {
        var response = await coach.Http.PostAsJsonAsync("/payments/discounts", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<CoachDiscountDto>(WireOptions, TestContext.Current.CancellationToken))!;
    }

    private Task<HttpResponseMessage> UpdateAsync(Actor coach, Guid discountId, object body) =>
        coach.Http.PutAsJsonAsync($"/payments/discounts/{discountId}", body, TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> ArchiveAsync(Actor coach, Guid discountId) =>
        coach.Http.PostAsync($"/payments/discounts/{discountId}/archive", JsonContent.Create(new { }), TestContext.Current.CancellationToken);

    private async Task<CoachDiscountDto> GetAsync(Actor coach, Guid discountId) =>
        (await coach.Http.GetFromJsonAsync<CoachDiscountDto>(
            $"/payments/discounts/{discountId}", WireOptions, TestContext.Current.CancellationToken))!;

    [Fact]
    public async Task CreateAndUpdate_PackageScope_IsStoredAllPackagesOrTheGivenList()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var first = await CreatePackageAsync(coach);
        var second = await CreatePackageAsync(coach);

        var everyPackage = await CreateDiscountAsync(coach, DiscountBody(all: true));

        everyPackage.Scope.Should().Be(DiscountScope.AllPackages);
        everyPackage.PackageIds.Should().BeEmpty();

        var limited = await CreateDiscountAsync(coach, DiscountBody([first]));

        limited.Scope.Should().Be(DiscountScope.SelectedPackages);
        limited.PackageIds.Should().Equal(first);
        (await GetAsync(coach, limited.DiscountId)).PackageIds.Should().Equal(first);

        (await UpdateAsync(coach, limited.DiscountId, DiscountBody([second], name: "Renamed"))).StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await GetAsync(coach, limited.DiscountId);
        updated.Name.Should().Be("Renamed");
        updated.PackageIds.Should().Equal(second);
    }

    [Fact]
    public async Task Create_InvalidBody_Returns400()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);

        var response = await coach.Http.PostAsJsonAsync(
            "/payments/discounts", DiscountBody(), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_ForeignUnknownOrArchivedPackage_Returns400WithIdenticalCode()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var other = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var foreignPackage = await CreatePackageAsync(other);
        var archivedPackage = await CreatePackageAsync(coach);
        await coach.Http.PostAsync($"/payments/packages/{archivedPackage}/archive", JsonContent.Create(new { }), TestContext.Current.CancellationToken);

        foreach (var packageId in new[] { foreignPackage, Guid.NewGuid(), archivedPackage })
        {
            var response = await coach.Http.PostAsJsonAsync(
                "/payments/discounts", DiscountBody([packageId]), TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ProblemCodeAsync(response)).Should().Be("DISCOUNT_PACKAGE_INVALID");
        }
    }

    [Fact]
    public async Task Get_ClientWithTermsUsingTheDiscount_CountsItInUsedBy()
    {
        var world = await ClientPayments.ClientPaymentFixtures.CreateWorldAsync(factory);
        var package = await ClientPayments.ClientPaymentFixtures.CreatePackageAsync(world.Coach);
        var used = await ClientPayments.ClientPaymentFixtures.CreateDiscountAsync(world.Coach, name: "Used");
        var unused = await ClientPayments.ClientPaymentFixtures.CreateDiscountAsync(world.Coach, name: "Unused");
        await ClientPayments.ClientPaymentFixtures.SetTermsOkAsync(world, package, used);

        (await GetAsync(world.Coach, used)).UsedByCount.Should().Be(1);
        (await GetAsync(world.Coach, unused)).UsedByCount.Should().Be(0);
    }

    [Fact]
    public async Task Create_FixedDiscountLimitedToAPackageInAnotherCurrency_Returns400CurrencyMismatch()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var euroPackage = await CreatePackageAsync(coach, "EUR");

        var response = await coach.Http.PostAsJsonAsync(
            "/payments/discounts",
            DiscountBody([euroPackage], type: "FixedAmount", currency: "CZK"),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(response)).Should().Be("CURRENCY_MISMATCH");
    }

    [Fact]
    public async Task Archive_IsIdempotentAndAnArchivedDiscountRefusesUpdatesWith409()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var discount = await CreateDiscountAsync(coach, DiscountBody(all: true));

        var first = await ArchiveAsync(coach, discount.DiscountId);
        var second = await ArchiveAsync(coach, discount.DiscountId);

        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        second.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetAsync(coach, discount.DiscountId)).ArchivedAt.Should().NotBeNull();

        var update = await UpdateAsync(coach, discount.DiscountId, DiscountBody(all: true));

        update.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(update)).Should().Be("DISCOUNT_ARCHIVED");
    }

    [Fact]
    public async Task ForeignOrUnknownDiscount_Returns404OnGetUpdateAndArchive()
    {
        var owner = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var intruder = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var discount = await CreateDiscountAsync(owner, DiscountBody(all: true));

        foreach (var discountId in new[] { discount.DiscountId, Guid.NewGuid() })
        {
            (await intruder.Http.GetAsync($"/payments/discounts/{discountId}", TestContext.Current.CancellationToken))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await UpdateAsync(intruder, discountId, DiscountBody(all: true, name: "Hijacked"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ArchiveAsync(intruder, discountId)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        var untouched = await GetAsync(owner, discount.DiscountId);
        untouched.ArchivedAt.Should().BeNull();
        untouched.Name.Should().Be("Spring sale");
    }
}
