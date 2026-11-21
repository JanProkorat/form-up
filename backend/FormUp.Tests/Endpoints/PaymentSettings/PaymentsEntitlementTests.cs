using System.Net;
using System.Net.Http.Json;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.PaymentSettings;

/// <summary>
/// The payments entitlement gate on every coach-facing payments endpoint: settings, bank account,
/// packages, discounts and client payments. Which subscription states lock the flag is
/// <c>EntitlementServiceTests</c>' job; this proves each endpoint consults it.
/// </summary>
[Collection(TestCollection.Shard4)]
public class PaymentsEntitlementTests(FitnessApiFactory factory)
{
    [Fact]
    public async Task CoachEndpoints_PlanWithoutPayments_Return403PaymentsNotEntitledAndWriteNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var coach = await TestActors.Trainer(factory).WithPassword(Password).CreateAsync(ct);
        var client = await TestActors.Client(factory).CreateAsync(ct);
        await TestActors.Link(factory, coach, client).CreateAsync(ct);
        await SubscribeAsync(factory, coach, canUsePayments: false);

        var anyId = Guid.NewGuid();
        var clientRoute = $"/payments/clients/{client.PublicId}";
        var package = new { Name = "Online coaching", Price = 1500m, Billing = "Monthly" };
        var discount = new { Name = "Spring sale", Type = "Percentage", Value = 10m, Duration = "EveryPayment", AppliesToAllPackages = true };

        (string Method, string Route, object? Body)[] calls =
        [
            ("GET", "/payments/settings", null),
            ("PUT", "/payments/settings", new { PaymentDueDays = 7, MessageFormat = "PackageName", VariableSymbolFormat = "YearMonthClientNumber" }),
            ("PUT", "/payments/bank-account", new { Iban = ValidIban, AccountHolder = "Martin Král", Password }),
            ("POST", "/payments/bank-account/code", null),
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
            ("GET", $"{clientRoute}/summary", null),
            ("GET", $"{clientRoute}/payments", null),
            ("GET", $"{clientRoute}/history", null),
            ("PUT", $"{clientRoute}/terms", new { PackageId = anyId }),
            ("POST", $"{clientRoute}/one-off", new { Title = "Workshop", Amount = 300m, DueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10) }),
            ("POST", $"{clientRoute}/payments/{anyId}/mark-paid", new { }),
            ("POST", $"{clientRoute}/payments/{anyId}/waive", new { }),
            ("POST", $"{clientRoute}/payments/{anyId}/request", new { SendEmail = true })
        ];

        foreach (var (method, route, body) in calls)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), route);

            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            var response = await coach.Http.SendAsync(request, ct);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {route}");
            (await ProblemCodeAsync(response)).Should().Be("PAYMENTS_NOT_ENTITLED", $"{method} {route}");
        }

        (await ReadSettingsAsync(factory, coach)).Should().BeNull();
    }
}
