using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Enums;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using static FormUp.Tests.Endpoints.ClientPayments.ClientPaymentFixtures;
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.ClientPayments;

/// <summary>
/// Link, ownership and removed-role checks shared by every coach-facing client payment endpoint, each proven
/// across all of them in one arrange. The entitlement gate is in <c>PaymentsEntitlementTests</c>.
/// </summary>
[Collection(TestCollection.Shard2)]
public class ClientPaymentAccessTests(FitnessApiFactory factory)
{
    private static readonly string[] PaymentActions = ["mark-paid", "waive", "request"];

    private static (string Method, string Path, object? Body)[] ClientCalls(Guid paymentId) =>
    [
        ("GET", "summary", null),
        ("GET", "payments", null),
        ("GET", "history", null),
        ("PUT", "terms", new { PackageId = Guid.NewGuid() }),
        ("POST", "one-off", new { Title = "Workshop", Amount = 300m, DueDate = Today.AddDays(10) }),
        ("POST", $"payments/{paymentId}/mark-paid", new { }),
        ("POST", $"payments/{paymentId}/waive", new { }),
        ("POST", $"payments/{paymentId}/request", new { SendEmail = true })
    ];

    private static async Task<HttpResponseMessage> SendAsync(Actor coach, Guid clientPublicId, string method, string path, object? body)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/payments/clients/{clientPublicId}/{path}");

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await coach.Http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CoachEndpoints_ClientWithNoLinkOrAnEndedLink_Return404()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await CreateWorldAsync(factory);
        var stranger = await TestActors.Client(factory).CreateAsync(ct);
        var former = await TestActors.Client(factory).CreateAsync(ct);
        await TestActors.Link(factory, world.Coach, former).Inactive().CreateAsync(ct);

        foreach (var client in new[] { stranger, former })
        {
            foreach (var (method, path, body) in ClientCalls(Guid.NewGuid()))
            {
                var response = await SendAsync(world.Coach, client.PublicId, method, path, body);

                response.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{method} {path}");
            }
        }
    }

    [Fact]
    public async Task PaymentActions_PaymentOfAnotherCoachOrOfAnotherClient_Return404AndChangeNothing()
    {
        var world = await CreateWorldAsync(factory);
        var otherCoach = await CreateWorldAsync(factory);
        var foreignRows = await StartMonthlyAsync(otherCoach);
        var sameCoach = world with { Client = await AddClientAsync(world) };
        var sameCoachRows = await StartMonthlyAsync(sameCoach);

        foreach (var paymentId in new[] { foreignRows[0].PaymentId, sameCoachRows[0].PaymentId })
        {
            foreach (var action in PaymentActions)
            {
                var response = await PostActionAsync(world, paymentId, action, new { SendEmail = true });

                response.StatusCode.Should().Be(HttpStatusCode.NotFound, action);
            }

            (await ReadStoredAsync(factory, paymentId)).Status.Should().Be(ClientPaymentStatus.Upcoming);
        }

        var oneOffForForeignClient = await world.Coach.Http.PostAsJsonAsync(
            $"{otherCoach.Route}/one-off", new { Title = "Workshop", Amount = 300m, DueDate = Today.AddDays(10) }, TestContext.Current.CancellationToken);
        oneOffForForeignClient.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Writes_CoachWhoseLastCoachRoleWasRemoved_AreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var coach = await TestActors.Trainer(factory).WithRemovedCoachRoles(UserRole.Trainer).CreateAsync(ct);
        await SubscribeAsync(factory, coach, canUsePayments: true);
        var client = await TestActors.Client(factory).CreateAsync(ct);
        await TestActors.Link(factory, coach, client).CreateAsync(ct);

        foreach (var (method, path, body) in ClientCalls(Guid.NewGuid()).Where(c => c.Method != "GET"))
        {
            var response = await SendAsync(coach, client.PublicId, method, path, body);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {path}");
        }
    }
}
