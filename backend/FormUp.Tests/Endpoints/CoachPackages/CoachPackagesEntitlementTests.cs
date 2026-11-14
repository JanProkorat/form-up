using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FormUp.Tests.Endpoints.ClientPayments.ClientPaymentFixtures;
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.CoachPackages;

/// <summary>
/// Packages and client payments are two plan features: packages and discounts follow the packages flag, client
/// payments and payment settings follow the payments flag, and attaching a package to a client needs both.
/// </summary>
[Collection(TestCollection.Name)]
public class CoachPackagesEntitlementTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpResponseMessage> SendAsync(Actor coach, string method, string route, object? body = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), route);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await coach.Http.SendAsync(request, Ct);
    }

    private async Task<Actor> CoachAsync(bool canUsePayments, bool canUsePackages)
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.MoveToPlanAsync(factory, coach, plan =>
        {
            plan.CanUsePayments = canUsePayments;
            plan.CanUsePackages = canUsePackages;
        });

        return coach;
    }

    [Fact]
    public async Task PackageAndDiscountEndpoints_PlanWithoutPackages_Return403PaymentsNotEntitled()
    {
        var coach = await CoachAsync(canUsePayments: true, canUsePackages: false);
        var anyId = Guid.NewGuid();
        var package = new { Name = "Online coaching", Price = 1500m, Billing = "Monthly" };
        var discount = new { Name = "Spring sale", Type = "Percentage", Value = 10m, Duration = "EveryPayment", AppliesToAllPackages = true };

        (string Method, string Route, object? Body)[] calls =
        [
            ("GET", "/payments/packages", null),
            ("POST", "/payments/packages", package),
            ("GET", $"/payments/packages/{anyId}", null),
            ("PUT", $"/payments/packages/{anyId}", package),
            ("POST", $"/payments/packages/{anyId}/archive", new { }),
            ("GET", "/payments/discounts", null),
            ("POST", "/payments/discounts", discount),
            ("GET", $"/payments/discounts/{anyId}", null),
            ("PUT", $"/payments/discounts/{anyId}", discount),
            ("POST", $"/payments/discounts/{anyId}/archive", new { }),
        ];

        foreach (var (method, route, body) in calls)
        {
            var response = await SendAsync(coach, method, route, body);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {route}");
            (await ProblemCodeAsync(response)).Should().Be("PAYMENTS_NOT_ENTITLED", $"{method} {route}");
        }

        (await SendAsync(coach, "GET", "/payments/settings")).StatusCode.Should().Be(
            HttpStatusCode.OK, "payment settings stay on the payments flag");
    }

    [Fact]
    public async Task ClientPaymentAndSettingsEndpoints_PlanWithPackagesButWithoutPayments_Return403PaymentsNotEntitled()
    {
        var coach = await CoachAsync(canUsePayments: false, canUsePackages: true);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        var clientRoute = $"/payments/clients/{client.PublicId}";

        (string Method, string Route)[] calls =
        [
            ("GET", "/payments/settings"),
            ("GET", $"{clientRoute}/summary"),
            ("GET", $"{clientRoute}/payments"),
            ("GET", $"{clientRoute}/history"),
        ];

        foreach (var (method, route) in calls)
        {
            var response = await SendAsync(coach, method, route);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {route}");
            (await ProblemCodeAsync(response)).Should().Be("PAYMENTS_NOT_ENTITLED", $"{method} {route}");
        }

        // Packages and discounts still work without client payments.
        (await SendAsync(coach, "GET", "/payments/discounts")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CreatePackageAsync(coach)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task InviteWithPackage_PackagesWithoutClientPayments_Returns403AndStoresNothing()
    {
        var coach = await CoachAsync(canUsePayments: true, canUsePackages: true);
        var packageId = await CreatePackageAsync(coach);
        await CoachSubscriptionBuilder.MoveToPlanAsync(factory, coach, plan => plan.CanUsePayments = false);

        await AssertInviteRefusedAsync(coach, packageId);
    }

    [Fact]
    public async Task InviteWithPackage_ClientPaymentsWithoutPackages_Returns403AndStoresNothing()
    {
        var coach = await CoachAsync(canUsePayments: true, canUsePackages: true);
        var packageId = await CreatePackageAsync(coach);
        await CoachSubscriptionBuilder.MoveToPlanAsync(factory, coach, plan => plan.CanUsePackages = false);

        await AssertInviteRefusedAsync(coach, packageId);
    }

    [Fact]
    public async Task SetClientTerms_ClientPaymentsWithoutPackages_Returns403()
    {
        var coach = await CoachAsync(canUsePayments: true, canUsePackages: true);
        var packageId = await CreatePackageAsync(coach);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        await CoachSubscriptionBuilder.MoveToPlanAsync(factory, coach, plan => plan.CanUsePackages = false);

        var response = await SendAsync(coach, "PUT", $"/payments/clients/{client.PublicId}/terms", new { PackageId = packageId });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemCodeAsync(response)).Should().Be("PAYMENTS_NOT_ENTITLED");
    }

    private async Task AssertInviteRefusedAsync(Actor coach, Guid packageId)
    {
        var email = $"{Guid.NewGuid():N}@packages-gate.com";

        var response = await SendAsync(coach, "POST", "/trainer/pending-invites", new { Email = email, PackageId = packageId });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemCodeAsync(response)).Should().Be("PAYMENTS_NOT_ENTITLED");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.PendingInvites.AnyAsync(invite => invite.Email == email, Ct)).Should().BeFalse();
    }
}
