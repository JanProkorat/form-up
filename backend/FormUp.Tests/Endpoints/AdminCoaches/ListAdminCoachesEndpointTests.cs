using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.AdminCoaches.ListAdminCoaches;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.AdminCoaches;

/// <summary>
/// Integration tests for <c>GET /admin/coaches</c> (<c>ListAdminCoachesEndpoint</c>). The database is shared, so list
/// assertions filter to the test's own coaches by an email token, and stats assert deltas.
/// </summary>
[Collection(TestCollection.Name)]
public class ListAdminCoachesEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewToken() => Guid.NewGuid().ToString("N")[..12];

    private Task<Actor> CreateCoachAsync(string token, string suffix, string? lastName = null) =>
        TestActors.Trainer(factory)
            .WithEmail($"{token}-{suffix}@coaches-fixture.com")
            .WithName($"First{suffix}", lastName ?? $"Last{token}")
            .CreateAsync(Ct);

    private async Task<(ListAdminCoachesResponse Body, int Total)> ListAsync(Actor admin, string query)
    {
        var response = await admin.Http.GetAsync($"/admin/coaches?{query}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var total = int.Parse(response.Headers.GetValues("X-Total-Count").Single());

        return ((await response.Content.ReadFromJsonAsync<ListAdminCoachesResponse>(JsonOptions, Ct))!, total);
    }

    private static string[] Emails(ListAdminCoachesResponse body) => body.Items.Select(item => item.Email!).ToArray();

    [Fact]
    public async Task List_Stats_CountEveryBucketOnceAndSumRevenueByCurrencyFromEachCoachsOwnPriceRow()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var currency = Guid.NewGuid().ToString("N")[..3].ToUpperInvariant();
        var planA = await PaidPlanBuilder.CreateAsync(
            factory, currency, priceMinorUnits: 30000, addOnMinorUnits: 10000, bothRolesIncluded: false);
        var planB = await PaidPlanBuilder.CreateAsync(
            factory, currency, priceMinorUnits: 120000, interval: BillingInterval.Annual);
        var before = (await ListAsync(admin, "pageSize=1")).Body.Stats;
        var now = DateTimeOffset.UtcNow;

        async Task SeedAsync(string plan, SubscriptionStatus status = SubscriptionStatus.Active, bool addOn = false,
            DateTimeOffset? givenFreeAt = null, DateTimeOffset? freeUntil = null, bool cancel = false)
        {
            var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
            await CoachSubscriptionBuilder.SubscribeToPlanAsync(
                factory, coach, plan, status, freeUntil, addOn, givenFreeAt, cancel);
        }

        await SeedAsync(planA);
        await SeedAsync(planA, addOn: true);
        await SeedAsync(planB);
        await SeedAsync(planA, SubscriptionStatus.PastDue);
        await SeedAsync(planA, cancel: true);
        await SeedAsync(planA, SubscriptionStatus.Trialing);
        await SeedAsync(planA, givenFreeAt: now.AddDays(-3));
        await TestActors.Trainer(factory).CreateAsync(Ct);
        await SeedAsync(planA, givenFreeAt: now.AddDays(-30), freeUntil: now.AddDays(-1));
        await SeedAsync(planB, addOn: true);

        // The plan's price changes: coaches already on it keep paying the row they were sold.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var oldRow = await db.SubscriptionPlanPrices.SingleAsync(p => p.SubscriptionPlan.Code == planA && p.ValidTo == null, Ct);
            oldRow.ValidTo = DateTime.UtcNow;
            db.SubscriptionPlanPrices.Add(new SubscriptionPlanPrice
            {
                SubscriptionPlanId = oldRow.SubscriptionPlanId,
                PriceMinorUnits = 99900,
                SecondRoleAddOnPriceMinorUnits = 10000,
                Currency = currency,
                ValidFrom = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        await SeedAsync(planA);

        var after = (await ListAsync(admin, "pageSize=1")).Body.Stats;

        (after.AllCount - before.AllCount).Should().Be(11);
        (after.PayingCount - before.PayingCount).Should().Be(8, "Payment failed, Cancelling and Trialing coaches are paying too");
        (after.PaymentFailedCount - before.PaymentFailedCount).Should().Be(1);
        (after.CancellingCount - before.CancellingCount).Should().Be(1);
        (after.FreeCount - before.FreeCount).Should().Be(2, "no row, and a gift that ended");
        (after.GivenFreeCount - before.GivenFreeCount).Should().Be(1);
        (after.PayingCount + after.FreeCount + after.GivenFreeCount).Should().Be(after.AllCount);
        after.PayingByPlan.Single(row => row.Plan.Code == planA).Count.Should().Be(6);
        after.PayingByPlan.Single(row => row.Plan.Code == planB).Count.Should().Be(2);

        // Monthly 30000 + 40000 (add-on) + 30000 + 30000 + 99900 (new row), plus 240000 annual / 12.
        // Trialing, given-free and Free coaches add nothing, and an add-on is not charged on a plan with both roles included.
        after.MonthlyRevenue.Single(row => row.Currency == currency).AmountMinorUnits.Should().Be(249900);

        var filtered = (await ListAsync(admin, "status=Free&search=no-such-coach-anywhere&page=3&pageSize=1")).Body.Stats;
        filtered.Should().BeEquivalentTo(after, "stats cover all coaches whatever the filter, search and page");
    }

    [Fact]
    public async Task List_EachStatusFilter_ReturnsOnlyItsBucketAndEveryRowCarriesItsOwnStatus()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var token = NewToken();
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        var now = DateTimeOffset.UtcNow;
        var expected = new Dictionary<string, AdminCoachStatus>
        {
            ["paying"] = AdminCoachStatus.Paying,
            ["failed"] = AdminCoachStatus.PaymentFailed,
            ["cancelling"] = AdminCoachStatus.Cancelling,
            ["free"] = AdminCoachStatus.Free,
            ["canceled"] = AdminCoachStatus.Free,
            ["gift"] = AdminCoachStatus.GivenFree,
        };

        foreach (var suffix in expected.Keys)
        {
            var coach = await CreateCoachAsync(token, suffix);

            switch (suffix)
            {
                case "paying":
                    await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);
                    break;
                case "failed":
                    await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, SubscriptionStatus.PastDue);
                    break;
                case "cancelling":
                    await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, cancelAtPeriodEnd: true);
                    break;
                case "canceled":
                    await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, SubscriptionStatus.Canceled);
                    break;
                case "gift":
                    await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, givenFreeAt: now.AddDays(-1));
                    break;
            }
        }

        string[] WithSuffixes(params string[] suffixes) => suffixes.Select(suffix => $"{token}-{suffix}@coaches-fixture.com").ToArray();

        var cases = new (AdminCoachStatusFilter Filter, string[] Expected)[]
        {
            (AdminCoachStatusFilter.All, WithSuffixes("paying", "failed", "cancelling", "free", "canceled", "gift")),
            (AdminCoachStatusFilter.Paying, WithSuffixes("paying", "failed", "cancelling")),
            (AdminCoachStatusFilter.PaymentFailed, WithSuffixes("failed")),
            (AdminCoachStatusFilter.Cancelling, WithSuffixes("cancelling")),
            (AdminCoachStatusFilter.Free, WithSuffixes("free", "canceled")),
            (AdminCoachStatusFilter.GivenFree, WithSuffixes("gift")),
        };

        foreach (var (filter, expectedEmails) in cases)
        {
            var (body, total) = await ListAsync(admin, $"status={filter}&search={token}&pageSize=100");

            Emails(body).Should().BeEquivalentTo(expectedEmails, $"filter {filter}");
            total.Should().Be(expectedEmails.Length, $"X-Total-Count counts the filtered coaches ({filter})");

            foreach (var item in body.Items)
            {
                var suffix = item.Email!.Split('-')[1].Split('@')[0];
                item.Status.Should().Be(expected[suffix], $"row {suffix} under filter {filter}");
            }
        }
    }

    [Fact]
    public async Task List_Search_MatchesNameOrEmailIgnoringCase()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var token = NewToken();
        var coach = await TestActors.Trainer(factory)
            .WithEmail($"mail-{token}@coaches-fixture.com")
            .WithName($"Alba{token}", "Quill")
            .CreateAsync(Ct);
        await TestActors.Trainer(factory).WithEmail($"other-{token}@coaches-fixture.com").WithName("Someone", "Else").CreateAsync(Ct);

        (await ListAsync(admin, $"search=ALBA{token.ToUpperInvariant()}")).Body.Items.Select(item => item.CoachId)
            .Should().Equal(coach.UserId);
        (await ListAsync(admin, $"search=quill&pageSize=100")).Body.Items.Select(item => item.CoachId).Should().Contain(coach.UserId);
        (await ListAsync(admin, $"search=Alba{token}%20Quill")).Body.Items.Should().ContainSingle("the full name matches too");
        (await ListAsync(admin, $"search=MAIL-{token}")).Body.Items.Select(item => item.CoachId).Should().Equal(coach.UserId);
        (await ListAsync(admin, $"search={token}")).Body.Items.Should().HaveCount(2);
        (await ListAsync(admin, $"search=nobody-{token}")).Body.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task List_Paging_SlicesTheFilteredCoachesInStableOrder()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var token = NewToken();

        foreach (var index in Enumerable.Range(0, 5))
        {
            await CreateCoachAsync(token, index.ToString(), lastName: $"Page{token}");
        }

        var first = await ListAsync(admin, $"search={token}&page=1&pageSize=2");
        var second = await ListAsync(admin, $"search={token}&page=2&pageSize=2");
        var third = await ListAsync(admin, $"search={token}&page=3&pageSize=2");

        first.Total.Should().Be(5);
        Emails(first.Body).Should().Equal($"{token}-0@coaches-fixture.com", $"{token}-1@coaches-fixture.com");
        Emails(second.Body).Should().Equal($"{token}-2@coaches-fixture.com", $"{token}-3@coaches-fixture.com");
        Emails(third.Body).Should().Equal($"{token}-4@coaches-fixture.com");
    }

    [Fact]
    public async Task List_Rows_ShowRolesPlanCapAndDates_AndCountOnlyLiveClients()
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var token = NewToken();
        var plan = await PaidPlanBuilder.CreateAsync(factory, maxActiveClients: 10);
        var periodEnd = DateTimeOffset.UtcNow.AddDays(12);
        var giftEnd = DateTimeOffset.UtcNow.AddDays(40);

        var paying = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist)
            .WithEmail($"{token}-paying@coaches-fixture.com")
            .WithRemovedCoachRoles(UserRole.Nutritionist)
            .CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, paying, plan, currentPeriodEndsAt: periodEnd);
        var clientA = await TestActors.Client(factory).CreateAsync(Ct);
        var clientB = await TestActors.Client(factory).CreateAsync(Ct);
        var clientEnded = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, paying, clientA).CreateAsync(Ct);
        await TestActors.Link(factory, paying, clientB).CreateAsync(Ct);
        await TestActors.Link(factory, paying, clientEnded).Inactive().CreateAsync(Ct);

        var free = await CreateCoachAsync(token, "free");
        var gift = await CreateCoachAsync(token, "gift");
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory, gift, plan, freeUntil: giftEnd, givenFreeAt: DateTimeOffset.UtcNow.AddDays(-1), currentPeriodEndsAt: periodEnd);

        int? freeCap;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            freeCap = await db.SubscriptionPlans.Where(p => p.Code == "free").Select(p => p.MaxActiveClients).SingleAsync(Ct);
        }

        var rows = (await ListAsync(admin, $"search={token}&pageSize=100")).Body.Items.ToDictionary(item => item.CoachId);

        var payingRow = rows[paying.UserId];
        payingRow.Roles.Should().Equal("Trainer");
        payingRow.Plan.Code.Should().Be(plan);
        payingRow.Status.Should().Be(AdminCoachStatus.Paying);
        payingRow.ClientsUsed.Should().Be(2, "clients are counted, and an ended link not at all");
        payingRow.ClientsMax.Should().Be(10);
        payingRow.CurrentPeriodEndsAt.Should().BeCloseTo(periodEnd, TimeSpan.FromSeconds(1));
        payingRow.GivenFreeUntil.Should().BeNull();
        payingRow.Promotion.Should().BeNull();

        var freeRow = rows[free.UserId];
        freeRow.Plan.Code.Should().Be("free");
        freeRow.ClientsUsed.Should().Be(0);
        freeRow.ClientsMax.Should().Be(freeCap);
        freeRow.CurrentPeriodEndsAt.Should().BeNull();

        var giftRow = rows[gift.UserId];
        giftRow.Status.Should().Be(AdminCoachStatus.GivenFree);
        giftRow.Plan.Code.Should().Be(plan);
        giftRow.GivenFreeUntil.Should().BeCloseTo(giftEnd, TimeSpan.FromSeconds(1));
        giftRow.CurrentPeriodEndsAt.Should().BeNull("a gift renews nothing");
    }

    [Theory]
    [InlineData("pageSize=0")]
    [InlineData("status=Nonsense")]
    public async Task List_InvalidQuery_Returns400(string query)
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);

        var response = await admin.Http.GetAsync($"/admin/coaches?{query}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
