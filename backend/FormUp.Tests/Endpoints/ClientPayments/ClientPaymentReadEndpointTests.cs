using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Client.GetClientPayments;
using FormUp.Application.Features.ClientPayments.GetClientPaymentHistory;
using FormUp.Application.Features.ClientPayments.GetClientPaymentSummary;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using static FormUp.Tests.Endpoints.ClientPayments.ClientPaymentFixtures;
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.ClientPayments;

/// <summary>
/// Integration tests for the coach's read endpoints: payment summary, payment list and change history.
/// </summary>
[Collection(TestCollection.Shard2)]
public class ClientPaymentReadEndpointTests(FitnessApiFactory factory)
{
    private async Task<GetClientPaymentSummaryResponse> GetSummaryAsync(PaymentWorld world)
    {
        var response = await world.Coach.Http.GetAsync($"{world.Route}/summary", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await ReadAsync<GetClientPaymentSummaryResponse>(response);
    }

    private static async Task<GetClientPaymentHistoryResponse> GetHistoryAsync(Actor coach, Actor client)
    {
        var response = await coach.Http.GetAsync($"/payments/clients/{client.PublicId}/history", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await ReadAsync<GetClientPaymentHistoryResponse>(response);
    }

    [Fact]
    public async Task Reads_ClientWithoutTerms_HaveNoRowsNoPackageNoHistoryAndKnowTheBankAccountState()
    {
        var world = await CreateWorldAsync(factory, bankAccount: false);

        var summary = await GetSummaryAsync(world);

        summary.Terms.Should().BeNull();
        summary.Price.Should().BeNull();
        summary.NextPayment.Should().BeNull();
        summary.BankAccountSet.Should().BeFalse();
        summary.ClientSince.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
        (await ListAsync(world)).Should().BeEmpty();
        (await GetHistoryAsync(world.Coach, world.Client)).Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task SummaryAndList_WithTermsAndDiscount_ShowTheBreakdownTheMessageAndWhatIsPaid()
    {
        var world = await CreateWorldAsync(factory);
        var discountId = await CreateDiscountAsync(world.Coach, 30m);
        var rows = await StartMonthlyAsync(world, 2500m, discountId);

        var summary = await GetSummaryAsync(world);

        summary.Terms!.PackageName.Should().Be("Online coaching");
        summary.Terms.DiscountName.Should().Be("Friends & family");
        summary.Price!.BasePrice.Should().Be(2500m);
        summary.Price.DiscountAmount.Should().Be(750m);
        summary.Price.Amount.Should().Be(1750m);
        summary.NextPeriodStart.Should().Be(rows[1].PeriodStart);
        summary.NextPayment!.PaymentId.Should().Be(rows[0].PaymentId);
        summary.NextPayment.Amount.Should().Be(1750m);
        summary.BankAccountSet.Should().BeTrue();

        var list = await ListAsync(world);
        list.Select(p => p.DueDate).Should().BeInDescendingOrder();
        rows[0].Message.Should().Be($"Online coaching {Today.Month:D2}/{Today.Year}");

        (await PostActionAsync(world, rows[0].PaymentId, "mark-paid")).StatusCode.Should().Be(HttpStatusCode.OK);

        var afterPaid = await GetSummaryAsync(world);
        afterPaid.PaidCount.Should().Be(1);
        afterPaid.TotalCount.Should().Be(1);
        afterPaid.PaidUntil.Should().Be(rows[0].PeriodEnd);
        afterPaid.NextPayment!.PaymentId.Should().Be(rows[1].PaymentId);
    }

    [Fact]
    public async Task Overdue_RequestedPaymentPastItsDueDate_IsDerivedForCoachAndClientButNeverStored()
    {
        var world = await CreateWorldAsync(factory);
        await StartMonthlyAsync(world);
        var late = await SeedPastPaymentAsync(world, Today.AddDays(-3), ClientPaymentStatus.Requested);
        var dueToday = await SeedPastPaymentAsync(world, Today, ClientPaymentStatus.Requested);

        var rows = await ListAsync(world);

        rows.Single(p => p.PaymentId == late).Status.Should().Be(ClientPaymentStatus.Overdue);
        rows.Single(p => p.PaymentId == dueToday).Status.Should().Be(ClientPaymentStatus.Requested, "a payment due today is not late yet");
        (await ReadStoredAsync(factory, late)).Status.Should().Be(ClientPaymentStatus.Requested, "overdue is derived and never stored");

        var own = await world.Client.Http.GetFromJsonAsync<GetClientPaymentsResponse>("/client/payments", Wire, TestContext.Current.CancellationToken);
        own!.Coaches.Single().Requested.Single(p => p.PaymentId == late).Status.Should().Be(ClientPaymentStatus.Overdue);
    }

    [Fact]
    public async Task History_ListsTheCallingCoachsActionsNewestFirstWithoutAnIbanOrClientName()
    {
        var world = await CreateWorldAsync(factory);
        var rows = await StartMonthlyAsync(world);
        (await PostActionAsync(world, rows[0].PaymentId, "mark-paid")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostActionAsync(world, rows[1].PaymentId, "waive", new { Reason = "Holiday" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await world.Coach.Http.GetAsync($"{world.Route}/history", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var history = await ReadAsync<GetClientPaymentHistoryResponse>(response);
        history.Entries.Select(e => e.Action).Should().Equal("WaiveClientPayment", "MarkClientPaymentPaid", "SetClientPaymentTerms");
        history.Entries[0].Detail.Should().Be("Holiday");
        history.Entries[0].PaymentId.Should().Be(rows[1].PaymentId);
        history.Entries[1].PaymentId.Should().Be(rows[0].PaymentId);
        history.Entries[2].Detail.Should().Be("Online coaching");
        history.Entries[1].Currency.Should().Be(FormUp.Application.Domain.Enums.PaymentCurrency.CZK);
        history.Entries[2].Currency.Should().Be(FormUp.Application.Domain.Enums.PaymentCurrency.CZK);
        history.Entries[0].Currency.Should().BeNull("a waive entry carries no amount");

        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.Should().NotContain(ValidIban).And.NotContain("Svobodov");

        // A second coach of the same client sees none of the first coach's entries.
        var secondCoach = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        await SubscribeAsync(factory, secondCoach, canUsePayments: true);
        await TestActors.Link(factory, secondCoach, world.Client).AsRole(UserRole.Nutritionist)
            .CreateAsync(TestContext.Current.CancellationToken);

        (await GetHistoryAsync(secondCoach, world.Client)).Entries.Should().BeEmpty();
    }
}
