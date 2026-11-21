using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FormUp.Tests.Endpoints.ClientPayments.ClientPaymentFixtures;

namespace FormUp.Tests.Endpoints.Client.Collaborations;

/// <summary>
/// Integration tests for the first-payment block of <c>GET /client/collaborations</c>.
/// </summary>
[Collection(TestCollection.Shard3)]
public class GetCollaborationsFirstPaymentIntegrationTests(FitnessApiFactory factory)
{
    private record CollaborationsResult(List<CollaborationItem> Collaborations);

    private record CollaborationItem(Guid ProfessionalPublicId, FirstPaymentItem? FirstPayment);

    private record FirstPaymentItem(
        string? PackageName, decimal? Amount, string Currency, string Status, Guid? PaymentId, DateOnly DueDate);

    private record ClientPaymentsResult(List<ClientPaymentCoachItem> Coaches);

    private record ClientPaymentCoachItem(List<ClientPaymentItem> Requested, List<ClientPaymentItem> Paid);

    private record ClientPaymentItem(Guid PaymentId);

    private async Task<FirstPaymentItem?> GetFirstPaymentAsync(Actor client, Actor coach)
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await client.Http.GetAsync("/client/collaborations", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CollaborationsResult>(cancellationToken: ct);

        return body!.Collaborations.Single(c => c.ProfessionalPublicId == coach.PublicId).FirstPayment;
    }

    /// <summary>A coach with a payments entitlement and a registered client who is not linked to them yet.</summary>
    private async Task<PaymentWorld> CreateUnlinkedWorldAsync()
    {
        var world = await CreateWorldAsync(factory);
        var client = await TestActors.Client(factory).WithName("Eva", "Svobodová").CreateAsync(TestContext.Current.CancellationToken);

        return world with { Client = client };
    }

    [Fact]
    public async Task GetAll_AcceptedInviteWithPackage_ReturnsUpcomingFirstPayment()
    {
        var world = await CreateUnlinkedWorldAsync();
        var packageId = await CreatePackageAsync(world.Coach, price: 2500m, name: "Online coaching");
        await AcceptInviteAsync(world, packageId);

        var firstPayment = await GetFirstPaymentAsync(world.Client, world.Coach);

        firstPayment.Should().NotBeNull();
        firstPayment!.Status.Should().Be(nameof(ClientPaymentStatus.Upcoming));
        firstPayment.Amount.Should().Be(2500m);
        firstPayment.Currency.Should().Be("CZK");
        firstPayment.PackageName.Should().Be("Online coaching");
        firstPayment.PaymentId.Should().BeNull();
    }

    [Fact]
    public async Task GetAll_NoTerms_ReturnsNullFirstPayment()
    {
        var world = await CreateWorldAsync(factory);

        (await GetFirstPaymentAsync(world.Client, world.Coach)).Should().BeNull();
    }

    [Fact]
    public async Task GetAll_FirstPaymentRequested_ReturnsRequestedWithPaymentIdFromClientPayments()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await CreateWorldAsync(factory);
        var rows = await StartMonthlyAsync(world, price: 1800m);
        (await PostActionAsync(world, rows[0].PaymentId, "request", new { SendEmail = true })).StatusCode.Should().Be(HttpStatusCode.OK);

        var firstPayment = await GetFirstPaymentAsync(world.Client, world.Coach);
        var clientPayments = await world.Client.Http.GetFromJsonAsync<ClientPaymentsResult>("/client/payments", Wire, ct);

        firstPayment!.Status.Should().Be(nameof(ClientPaymentStatus.Requested));
        firstPayment.Amount.Should().Be(1800m);
        firstPayment.PaymentId.Should().NotBeNull();
        clientPayments!.Coaches.SelectMany(c => c.Requested).Select(p => p.PaymentId).Should().Contain(firstPayment.PaymentId!.Value);
    }

    [Fact]
    public async Task GetAll_LaterRowRequested_FirstPaymentIsTheRequestedRowOfClientPayments()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await CreateWorldAsync(factory);
        var rows = await StartMonthlyAsync(world, price: 1400m);
        rows.Count.Should().BeGreaterThan(1, "the requested row must differ from the earliest period");
        var requestedRow = rows[^1];
        (await PostActionAsync(world, requestedRow.PaymentId, "request", new { SendEmail = true })).StatusCode.Should().Be(HttpStatusCode.OK);

        var firstPayment = await GetFirstPaymentAsync(world.Client, world.Coach);
        var clientPayments = await world.Client.Http.GetFromJsonAsync<ClientPaymentsResult>("/client/payments", Wire, ct);
        var requestedItem = clientPayments!.Coaches.SelectMany(c => c.Requested).Single();

        firstPayment!.PaymentId.Should().Be(requestedItem.PaymentId);
        firstPayment.PaymentId.Should().Be(requestedRow.PaymentId);
        firstPayment.Status.Should().Be(nameof(ClientPaymentStatus.Requested));
        firstPayment.Amount.Should().Be(1400m);
    }

    [Fact]
    public async Task GetAll_FirstPaymentRequestedAndPastDue_ReturnsOverdue()
    {
        var world = await CreateWorldAsync(factory);
        var rows = await StartMonthlyAsync(world, price: 900m);
        (await PostActionAsync(world, rows[0].PaymentId, "request", new { SendEmail = true })).StatusCode.Should().Be(HttpStatusCode.OK);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await db.ClientPayments.SingleAsync(p => p.PublicId == rows[0].PaymentId, TestContext.Current.CancellationToken);
            stored.DueDate = Today.AddDays(-5);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var firstPayment = await GetFirstPaymentAsync(world.Client, world.Coach);

        firstPayment!.Status.Should().Be(nameof(ClientPaymentStatus.Overdue));
        firstPayment.PaymentId.Should().NotBeNull();
    }

    [Fact]
    public async Task GetAll_FirstPaymentPaid_ReturnsPaid()
    {
        var world = await CreateWorldAsync(factory);
        var rows = await StartMonthlyAsync(world);
        (await PostActionAsync(world, rows[0].PaymentId, "mark-paid")).StatusCode.Should().Be(HttpStatusCode.OK);

        var firstPayment = await GetFirstPaymentAsync(world.Client, world.Coach);

        firstPayment!.Status.Should().Be(nameof(ClientPaymentStatus.Paid));
        firstPayment.PaymentId.Should().Be(rows[0].PaymentId);
    }

    [Fact]
    public async Task GetAll_ReturningClientWithNewTerms_ReturnsFirstPeriodOfCurrentTerms()
    {
        var world = await CreateWorldAsync(factory);
        var rows = await StartMonthlyAsync(world, price: 1000m);
        (await PostActionAsync(world, rows[0].PaymentId, "mark-paid")).StatusCode.Should().Be(HttpStatusCode.OK);
        await EndCollaborationAsync(world);
        var newPackageId = await CreatePackageAsync(world.Coach, price: 3300m, name: "Premium");
        await AcceptInviteAsync(world, newPackageId);

        var firstPayment = await GetFirstPaymentAsync(world.Client, world.Coach);

        firstPayment!.Status.Should().Be(nameof(ClientPaymentStatus.Upcoming));
        firstPayment.Amount.Should().Be(3300m);
        firstPayment.PackageName.Should().Be("Premium");
    }

    [Fact]
    public async Task GetAll_TwoClientsOfOneCoach_EachSeesOnlyOwnFirstPayment()
    {
        var world = await CreateWorldAsync(factory);
        await StartMonthlyAsync(world, price: 1111m);
        var other = world with { Client = await AddClientAsync(world) };
        var otherPackageId = await CreatePackageAsync(world.Coach, price: 2222m, name: "Other");
        await SetTermsOkAsync(other, otherPackageId);
        var otherRows = (await ListAsync(other)).OrderBy(p => p.PeriodStart).ToList();
        (await PostActionAsync(other, otherRows[0].PaymentId, "request", new { SendEmail = true })).StatusCode.Should().Be(HttpStatusCode.OK);

        var own = await GetFirstPaymentAsync(world.Client, world.Coach);
        var theirs = await GetFirstPaymentAsync(other.Client, world.Coach);

        own!.Amount.Should().Be(1111m);
        own.Status.Should().Be(nameof(ClientPaymentStatus.Upcoming));
        own.PaymentId.Should().BeNull();
        theirs!.Amount.Should().Be(2222m);
        theirs.Status.Should().Be(nameof(ClientPaymentStatus.Requested));
    }
}
