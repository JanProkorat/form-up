using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.Professionals;

/// <summary>
/// The derived "from" price on the public profile and the search list.
/// </summary>
[Collection(TestCollection.Name)]
public class FromMonthlyPriceTests(FitnessApiFactory factory)
{
    private sealed record ProfileBody(
        decimal? FromMonthlyPrice,
        PaymentCurrency? FromMonthlyPriceCurrency,
        PackageBillingInterval? FromPriceBilling,
        int? FromPriceIntervalWeeks);

    private sealed record SearchItem(Guid PublicId, decimal? FromMonthlyPrice, string? FromPriceBilling, int? FromPriceIntervalWeeks);

    private sealed record SearchBody(List<SearchItem> Items);

    private async Task AddPackageAsync(
        Actor coach,
        decimal price,
        PackageBillingInterval billing = PackageBillingInterval.Monthly,
        bool showOnProfile = true,
        bool archived = false,
        PaymentCurrency currency = PaymentCurrency.CZK)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.CoachPackages.Add(new CoachPackage
        {
            PublicId = Guid.NewGuid(),
            ProfessionalProfileId = coach.ProfileId,
            Name = "Package",
            Price = price,
            Currency = currency,
            Billing = billing,
            IntervalWeeks = billing == PackageBillingInterval.EveryNWeeks ? 2 : null,
            ShowOnProfile = showOnProfile,
            ArchivedAt = archived ? DateTime.UtcNow : null
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<ProfileBody> ReadProfileAsync(Actor coach, Actor client)
    {
        var response = await client.Http.GetAsync($"/professionals/{coach.PublicId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<ProfileBody>(
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            },
            TestContext.Current.CancellationToken))!;
    }

    private async Task SetProfileCurrencyAsync(Actor coach, PaymentCurrency currency)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.CoachPaymentSettings.Add(new CoachPaymentSettings
        {
            PublicId = Guid.NewGuid(),
            ProfessionalProfileId = coach.ProfileId,
            ProfileCurrency = currency
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PublicProfile_MixedPackages_ShowsTheLowestActiveVisibleInMainCurrency()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await AddPackageAsync(coach, 2500m);
        await AddPackageAsync(coach, 1800m);

        // Each of these is cheaper than 1800 and must be ignored.
        await AddPackageAsync(coach, 900m, archived: true);
        await AddPackageAsync(coach, 800m, showOnProfile: false);

        // A coach without a settings row has CZK as main currency, so a cheaper euro package is ignored.
        await AddPackageAsync(coach, 50m, currency: PaymentCurrency.EUR);

        var profile = await ReadProfileAsync(coach, client);

        profile.FromMonthlyPrice.Should().Be(1800m);
        profile.FromMonthlyPriceCurrency.Should().Be(PaymentCurrency.CZK);
        profile.FromPriceBilling.Should().Be(PackageBillingInterval.Monthly);
        profile.FromPriceIntervalWeeks.Should().BeNull();
    }

    [Fact]
    public async Task PublicProfile_OneOffPackageIsCheapest_ShowsItWithOneOffBilling()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await AddPackageAsync(coach, 1800m);
        await AddPackageAsync(coach, 700m, PackageBillingInterval.EveryNWeeks);
        await AddPackageAsync(coach, 600m, PackageBillingInterval.OneOff);

        var profile = await ReadProfileAsync(coach, client);

        profile.FromMonthlyPrice.Should().Be(600m);
        profile.FromMonthlyPriceCurrency.Should().Be(PaymentCurrency.CZK);
        profile.FromPriceBilling.Should().Be(PackageBillingInterval.OneOff);
        profile.FromPriceIntervalWeeks.Should().BeNull();
    }

    [Fact]
    public async Task PublicProfile_EveryNWeeksPackageIsCheapest_ShowsItWithItsIntervalWeeks()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await AddPackageAsync(coach, 1800m);
        await AddPackageAsync(coach, 900m, PackageBillingInterval.OneOff);
        await AddPackageAsync(coach, 700m, PackageBillingInterval.EveryNWeeks);

        var profile = await ReadProfileAsync(coach, client);

        profile.FromMonthlyPrice.Should().Be(700m);
        profile.FromMonthlyPriceCurrency.Should().Be(PaymentCurrency.CZK);
        profile.FromPriceBilling.Should().Be(PackageBillingInterval.EveryNWeeks);
        profile.FromPriceIntervalWeeks.Should().Be(2);
    }

    [Fact]
    public async Task PublicProfile_EuroMainCurrency_ShowsTheLowestEuroPackage()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await SetProfileCurrencyAsync(coach, PaymentCurrency.EUR);
        await AddPackageAsync(coach, 80m, currency: PaymentCurrency.EUR);
        await AddPackageAsync(coach, 60m, currency: PaymentCurrency.EUR);
        await AddPackageAsync(coach, 500m);

        var profile = await ReadProfileAsync(coach, client);

        profile.FromMonthlyPrice.Should().Be(60m);
        profile.FromMonthlyPriceCurrency.Should().Be(PaymentCurrency.EUR);
    }

    [Fact]
    public async Task Search_MixedCoaches_ResolvesEachPriceInOneBatch()
    {
        var tag = Guid.NewGuid().ToString("N");
        var priced = await TestActors.Trainer(factory).WithPayments().WithName("Priced", $"Batch{tag}").CreateAsync(TestContext.Current.CancellationToken);
        var locked = await TestActors.Trainer(factory).WithName("Locked", $"Batch{tag}").CreateAsync(TestContext.Current.CancellationToken);
        var bare = await TestActors.Trainer(factory).WithName("Bare", $"Batch{tag}").CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await AddPackageAsync(priced, 1500m);
        await AddPackageAsync(locked, 1200m);
        await SubscribeAsync(factory, locked, canUsePayments: false);

        var response = await client.Http.GetAsync($"/professionals/search?search=Batch{tag}", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<SearchBody>(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body!.Items.Single(i => i.PublicId == priced.PublicId).FromMonthlyPrice.Should().Be(1500m);
        body.Items.Single(i => i.PublicId == priced.PublicId).FromPriceBilling.Should().Be("Monthly");
        body.Items.Single(i => i.PublicId == locked.PublicId).FromMonthlyPrice.Should().BeNull();
        body.Items.Single(i => i.PublicId == bare.PublicId).FromMonthlyPrice.Should().BeNull();
    }
}
