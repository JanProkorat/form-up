using System.Net;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Client.Collaborations.GetAll;
using FormUp.Application.Features.Client.GetClientPayments;
using FormUp.Application.Features.ClientPayments.GetClientPaymentSummary;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FormUp.Tests.Endpoints.ClientPayments.ClientPaymentFixtures;

namespace FormUp.Tests.Endpoints.ClientPayments;

/// <summary>
/// Regression tests for #1767 (payment rows after a client reconnects to the same coach): same-day restart cleanup,
/// pricing under the new terms, and the earlier-collaboration marker.
/// </summary>
[Collection(TestCollection.Shard3)]
public class Issue1767ReconnectPaymentsReproTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<List<ClientPayment>> RowsAsync(PaymentWorld world)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientPayments.AsNoTracking()
            .Where(p => p.ClientPaymentAssignment.ProfessionalProfileId == world.Coach.ProfileId
                && p.ClientPaymentAssignment.ClientProfileId == world.Client.ProfileId
                && p.PeriodStart != null)
            .OrderBy(p => p.PeriodStart)
            .ToListAsync(Ct);
    }

    private async Task<List<ClientPaymentTerms>> TermsAsync(PaymentWorld world)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientPaymentTerms.AsNoTracking()
            .Where(t => t.ClientPaymentAssignment.ProfessionalProfileId == world.Coach.ProfileId
                && t.ClientPaymentAssignment.ClientProfileId == world.Client.ProfileId)
            .OrderBy(t => t.Id)
            .ToListAsync(Ct);
    }

    /// <summary>Moves the assignment's terms and periods back by whole days, as if they were made on an earlier day.</summary>
    private async Task AgeDaysAsync(PaymentWorld world, int days)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var assignmentId = await db.ClientPaymentAssignments
            .Where(a => a.ClientProfileId == world.Client.ProfileId && a.ProfessionalProfileId == world.Coach.ProfileId)
            .Select(a => a.Id).SingleAsync(Ct);

        foreach (var terms in await db.ClientPaymentTerms.Where(t => t.ClientPaymentAssignmentId == assignmentId).ToListAsync(Ct))
        {
            terms.EffectiveFromUtc = terms.EffectiveFromUtc.AddDays(-days);
            terms.EndsAtUtc = terms.EndsAtUtc?.AddDays(-days);
        }

        foreach (var period in await db.ClientPayments.Where(p => p.ClientPaymentAssignmentId == assignmentId).ToListAsync(Ct))
        {
            period.PeriodStart = period.PeriodStart?.AddDays(-days);
            period.PeriodEnd = period.PeriodEnd?.AddDays(-days);
            period.DueDate = period.DueDate.AddDays(-days);
            period.VariableSymbol = null;
        }

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// Symptom 3. The first collaboration's started period (today) is kept as history, so a same-day reconnect plans
    /// the new collaboration's first period one interval later.
    /// </summary>
    [Theory]
    [InlineData("Monthly", null)]
    [InlineData("EveryNWeeks", 5)]
    public async Task Reaccept_OnTheDayOfTheEnd_NewCollaborationsFirstPeriodStartsToday(string billing, int? weeks)
    {
        var world = await CreateWorldAsync(factory);
        var package = await CreatePackageAsync(world.Coach, 2500m, "Package", billing, weeks);
        await EndCollaborationAsync(world);
        await AcceptInviteAsync(world, package);
        await EndCollaborationAsync(world);
        var oldIds = (await RowsAsync(world)).Select(p => p.Id).ToHashSet();

        await AcceptInviteAsync(world, package);

        var newStarts = (await RowsAsync(world)).Where(p => !oldIds.Contains(p.Id)).Select(p => p.PeriodStart!.Value).Order().ToList();
        newStarts.Should().NotBeEmpty();
        newStarts[0].Should().Be(Today, "the new collaboration bills from the day it starts, not one interval later");
    }

    /// <summary>Symptom 3, second half: an Upcoming row of the ended collaboration is still around for the current period.</summary>
    [Fact]
    public async Task Reaccept_OnTheDayOfTheEnd_LeavesNoUpcomingRowOfTheEndedCollaboration()
    {
        var world = await CreateWorldAsync(factory);
        var package = await CreatePackageAsync(world.Coach, 2500m, "Package", "EveryNWeeks", 5);
        await EndCollaborationAsync(world);
        await AcceptInviteAsync(world, package);
        await EndCollaborationAsync(world);
        var oldIds = (await RowsAsync(world)).Select(p => p.Id).ToHashSet();

        await AcceptInviteAsync(world, package);

        (await RowsAsync(world)).Where(p => oldIds.Contains(p.Id) && p.Status == ClientPaymentStatus.Upcoming)
            .Select(p => (p.PeriodStart, p.Status, p.Amount)).Should().BeEmpty();
    }

    /// <summary>
    /// Symptom 2. A started period of an ended collaboration is the coach's to collect: the request goes through,
    /// the client sees it under the coach, and it is flagged as an earlier collaboration's.
    /// </summary>
    [Fact]
    public async Task Request_StartedPeriodOfAnEndedCollaboration_IsAcceptedAndShownToTheClientAsEarlier()
    {
        var world = await CreateWorldAsync(factory);
        var package = await CreatePackageAsync(world.Coach, 2500m, "Package");
        await EndCollaborationAsync(world);
        await AcceptInviteAsync(world, package);
        await EndCollaborationAsync(world);
        await AgeDaysAsync(world, 1);
        var old = (await RowsAsync(world)).Should().ContainSingle().Subject;
        old.Status.Should().Be(ClientPaymentStatus.Upcoming);
        old.PeriodStart.Should().Be(Today.AddDays(-1));

        var response = await PostActionAsync(world, old.PublicId, "request", new { SendNotification = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await ReadStoredAsync(factory, old.PublicId)).Status.Should().Be(ClientPaymentStatus.Requested);
        var clientView = await world.Client.Http.GetAsync("/client/payments", Ct);
        (await ReadAsync<GetClientPaymentsResponse>(clientView)).Coaches.SelectMany(c => c.Requested)
            .Should().ContainSingle(item => item.PaymentId == old.PublicId).Which.IsEarlierCollaboration.Should().BeTrue();
        (await ListAsync(world)).Single(row => row.PaymentId == old.PublicId).IsEarlierCollaboration.Should().BeTrue();
    }

    /// <summary>
    /// Symptom 2 after a reconnect: the earlier collaboration's row can still be requested, is flagged as earlier, and
    /// neither the client's first payment nor the coach's summary picks it.
    /// </summary>
    [Fact]
    public async Task Request_RowOfAnEarlierCollaboration_AfterAReconnect_IsAcceptedAndFlaggedEarlier()
    {
        var world = await CreateWorldAsync(factory);
        var package = await CreatePackageAsync(world.Coach, 2500m, "Package");
        await EndCollaborationAsync(world);
        await AcceptInviteAsync(world, package);
        await EndCollaborationAsync(world);
        await AgeDaysAsync(world, 1);
        var old = (await RowsAsync(world)).Should().ContainSingle().Subject;
        await AcceptInviteAsync(world, package);

        var response = await PostActionAsync(world, old.PublicId, "request", new { SendNotification = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var rows = await ListAsync(world);
        rows.Single(row => row.PaymentId == old.PublicId).IsEarlierCollaboration.Should().BeTrue();
        rows.Where(row => row.PaymentId != old.PublicId).Should().OnlyContain(row => !row.IsEarlierCollaboration);

        var collaborations = await ReadAsync<GetCollaborationsResponse>(await world.Client.Http.GetAsync("/client/collaborations", Ct));
        var firstPayment = collaborations.Collaborations.Single().FirstPayment;
        firstPayment.Should().NotBeNull();
        firstPayment!.PaymentId.Should().NotBe(old.PublicId);
        firstPayment.DueDate.Should().Be(Today);

        var summary = await ReadAsync<GetClientPaymentSummaryResponse>(await world.Coach.Http.GetAsync($"{world.Route}/summary", Ct));
        summary.NextPayment.Should().NotBeNull();
        summary.NextPayment!.PaymentId.Should().NotBe(old.PublicId);
        summary.PaidCount.Should().Be(0);
    }

    /// <summary>
    /// Same-day restart onto a different package with a first-payment discount: the new first period is priced
    /// under the new terms (price, discount, currency), not the ended ones.
    /// </summary>
    [Fact]
    public async Task Reaccept_OnTheDayOfTheEnd_WithAnotherPackageAndDiscount_PricesTheFirstPeriodUnderTheNewTerms()
    {
        var world = await CreateWorldAsync(factory);
        var oldPackage = await CreatePackageAsync(world.Coach, 2500m, "Old");
        var newPackage = await CreatePackageAsync(world.Coach, 400m, "New", currency: "EUR");
        var discount = await CreateDiscountAsync(world.Coach, 50m, "FirstPayments", 1);
        await SetTermsOkAsync(world, oldPackage);
        await EndCollaborationAsync(world);
        await AcceptInviteAsync(world, newPackage);
        await SetTermsOkAsync(world, newPackage, discount);

        var rows = await ListAsync(world);

        var first = rows.Single(row => row.PeriodStart == Today);
        first.PackageName.Should().Be("New");
        first.BasePrice.Should().Be(400m);
        first.Amount.Should().Be(200m);
        first.DiscountLabel.Should().NotBeNull();
        first.Currency.Should().Be(PaymentCurrency.EUR);
        first.IsEarlierCollaboration.Should().BeFalse();
        (await ReadStoredAsync(factory, first.PaymentId)).Currency.Should().Be(PaymentCurrency.EUR);
        rows.Should().OnlyContain(row => !row.IsEarlierCollaboration);
    }

    /// <summary>
    /// A period of the ended collaboration that was already requested on the restart day cannot be deleted: it
    /// stays as the coach's earlier row and the new collaboration's first period starts one interval later.
    /// </summary>
    [Fact]
    public async Task Reaccept_OnTheDayOfTheEnd_WithTheDaysPeriodRequested_KeepsItAsEarlierAndStartsOneIntervalLater()
    {
        var world = await CreateWorldAsync(factory);
        var package = await CreatePackageAsync(world.Coach, 2500m, "Package");
        await SetTermsOkAsync(world, package);
        var locked = (await ListAsync(world)).Single(row => row.PeriodStart == Today);
        (await PostActionAsync(world, locked.PaymentId, "request", new { SendEmail = true })).StatusCode.Should().Be(HttpStatusCode.OK);
        await EndCollaborationAsync(world);

        await AcceptInviteAsync(world, package);

        var stored = await ReadStoredAsync(factory, locked.PaymentId);
        stored.IsKeptAfterEnd.Should().BeTrue();
        stored.Status.Should().Be(ClientPaymentStatus.Requested);
        var rows = await ListAsync(world);
        rows.Single(row => row.PaymentId == locked.PaymentId).IsEarlierCollaboration.Should().BeTrue();
        rows.Where(row => row.PaymentId != locked.PaymentId).Select(row => row.PeriodStart).Should().Equal(Today.AddMonths(1));
        rows.Where(row => row.PaymentId != locked.PaymentId).Should().OnlyContain(row => !row.IsEarlierCollaboration);
    }

    /// <summary>A one-off payment made before the end is an earlier collaboration's; one added after the reconnect is not.</summary>
    [Fact]
    public async Task OneOffPayment_AddedAfterTheReconnect_IsNotMarkedEarlier()
    {
        var world = await CreateWorldAsync(factory);
        var package = await CreatePackageAsync(world.Coach, 2500m, "Package");
        await SetTermsOkAsync(world, package);
        var before = await world.Coach.Http.PostAsJsonAsync(
            $"{world.Route}/one-off", new { Title = "Before", Amount = 300m, DueDate = Today }, Ct);
        before.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        await EndCollaborationAsync(world);
        await AcceptInviteAsync(world, package);
        var after = await world.Coach.Http.PostAsJsonAsync(
            $"{world.Route}/one-off", new { Title = "After", Amount = 400m, DueDate = Today }, Ct);
        after.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);

        var rows = await ListAsync(world);

        rows.Single(row => row.Title == "Before").IsEarlierCollaboration.Should().BeTrue();
        rows.Single(row => row.Title == "After").IsEarlierCollaboration.Should().BeFalse();
        rows.Where(row => row.Kind == ClientPaymentKind.Recurring).Should().OnlyContain(row => !row.IsEarlierCollaboration);
    }

    /// <summary>
    /// Symptom 1 guard: every endpoint sequence tried (accept/end/accept, package change, ageing) closes all terms on
    /// end. Passes today; kept so the fix cannot regress it.
    /// </summary>
    [Fact]
    public async Task EndCollaboration_AfterPackageChangeAndReaccept_ClosesEveryTermsRow()
    {
        var world = await CreateWorldAsync(factory);
        var first = await CreatePackageAsync(world.Coach, 2500m, "First");
        var second = await CreatePackageAsync(world.Coach, 3000m, "Second");
        await EndCollaborationAsync(world);
        await AcceptInviteAsync(world, first);
        (await SetTermsAsync(world, second)).StatusCode.Should().Be(HttpStatusCode.OK);
        await EndCollaborationAsync(world);
        await AcceptInviteAsync(world, first);
        (await SetTermsAsync(world, second)).StatusCode.Should().Be(HttpStatusCode.OK);

        await EndCollaborationAsync(world);

        (await TermsAsync(world)).Should().OnlyContain(t => t.EndsAtUtc != null);
    }
}
