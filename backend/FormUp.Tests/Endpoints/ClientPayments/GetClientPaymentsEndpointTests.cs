using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Client.GetClientPayments;
using FormUp.Application.Features.ClientPayments.GetMyPayment;
using FormUp.Application.Features.ClientPayments.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FormUp.Tests.Endpoints.ClientPayments.ClientPaymentFixtures;
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.ClientPayments;

/// <summary>
/// Integration tests for the client's own payment endpoints: GET /client/payments and GET /client/payments/{PaymentId}.
/// </summary>
[Collection(TestCollection.Name)]
public class GetClientPaymentsEndpointTests(FitnessApiFactory factory)
{
    private async Task<GetClientPaymentsResponse> GetOwnAsync(Actor client)
    {
        var response = await client.Http.GetAsync("/client/payments", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await ReadAsync<GetClientPaymentsResponse>(response);
    }

    private static Task<HttpResponseMessage> GetOneAsync(Actor client, Guid paymentId) =>
        client.Http.GetAsync($"/client/payments/{paymentId}", TestContext.Current.CancellationToken);

    private static async Task<ClientPaymentRowDto> AddOneOffAsync(PaymentWorld world, string title, decimal amount) =>
        await ReadAsync<ClientPaymentRowDto>(await world.Coach.Http.PostAsJsonAsync(
            $"{world.Route}/one-off", new { Title = title, Amount = amount, DueDate = Today.AddDays(9) }, TestContext.Current.CancellationToken));

    [Fact]
    public async Task Get_ShowsOnlyRequestedAndPaidPayments_WithBankDetailsAndQrOnlyWhileRequested()
    {
        var world = await CreateWorldAsync(factory);
        var rows = await StartMonthlyAsync(world, 2500m);
        await PostActionAsync(world, rows[0].PaymentId, "request", new { SendEmail = true });
        await PostActionAsync(world, rows[0].PaymentId, "mark-paid", new { PaidOn = Today.AddDays(-1) });
        await PostActionAsync(world, rows[1].PaymentId, "request", new { SendEmail = true });
        var waived = await AddOneOffAsync(world, "Waived one", 50m);
        await PostActionAsync(world, waived.PaymentId, "waive");
        var upcoming = await AddOneOffAsync(world, "Upcoming one", 300m);

        var coach = (await GetOwnAsync(world.Client)).Coaches.Should().ContainSingle().Subject;

        coach.CoachName.Should().Be("Martin Král");
        coach.CollaborationEnded.Should().BeFalse();
        var requested = coach.Requested.Should().ContainSingle().Subject;
        requested.PaymentId.Should().Be(rows[1].PaymentId);
        requested.Status.Should().Be(ClientPaymentStatus.Requested);
        requested.Iban.Should().Be(ValidIban);
        requested.AccountHolder.Should().Be("Martin Král");
        requested.VariableSymbol.Should().Be(rows[1].VariableSymbol);
        requested.Amount.Should().Be(2500m);
        requested.QrPayload.Should().StartWith("SPD*1.0*ACC:CZ6508000000192000145399*AM:2500.00*CC:CZK")
            .And.Contain($"*X-VS:{rows[1].VariableSymbol}");

        var paid = coach.Paid.Should().ContainSingle().Subject;
        paid.PaymentId.Should().Be(rows[0].PaymentId);
        paid.Status.Should().Be(ClientPaymentStatus.Paid);
        paid.PaidOn.Should().Be(Today.AddDays(-1));
        paid.Iban.Should().BeNull();
        paid.QrPayload.Should().BeNull();

        var one = await GetOneAsync(world.Client, rows[1].PaymentId);
        one.StatusCode.Should().Be(HttpStatusCode.OK);
        var payment = await ReadAsync<GetMyPaymentResponse>(one);
        payment.CoachName.Should().Be("Martin Král");
        payment.Iban.Should().Be(ValidIban);
        payment.QrPayload.Should().StartWith("SPD*1.0*");

        (await GetOneAsync(world.Client, upcoming.PaymentId)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetOneAsync(world.Client, waived.PaymentId)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_AnotherClientOfTheSameCoach_NeitherListsNorOpensTheOthersPayment()
    {
        var world = await CreateWorldAsync(factory);
        var otherClient = world with { Client = await AddClientAsync(world) };
        var otherRows = await StartMonthlyAsync(otherClient);
        await PostActionAsync(otherClient, otherRows[0].PaymentId, "request", new { SendEmail = true });
        var rows = await StartMonthlyAsync(world);
        await PostActionAsync(world, rows[0].PaymentId, "request", new { SendEmail = true });

        var own = await GetOwnAsync(world.Client);

        own.Coaches.Single().Requested.Select(p => p.PaymentId).Should().Equal(rows[0].PaymentId);
        (await GetOneAsync(world.Client, otherRows[0].PaymentId)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_LinkEnded_KeepsThatCoachsPaymentsVisibleAndFlagsTheCoach()
    {
        var world = await CreateWorldAsync(factory);
        var rows = await StartMonthlyAsync(world);
        await PostActionAsync(world, rows[0].PaymentId, "request", new { SendEmail = true });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.ClientProfessionalLinks
                .Where(l => l.ProfessionalProfileId == world.Coach.ProfileId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ConnectionState, ConnectionState.Ended), TestContext.Current.CancellationToken);
        }

        var coach = (await GetOwnAsync(world.Client)).Coaches.Should().ContainSingle().Subject;

        coach.CollaborationEnded.Should().BeTrue();
        coach.Requested.Should().ContainSingle().Which.PaymentId.Should().Be(rows[0].PaymentId);
        (await GetOneAsync(world.Client, rows[0].PaymentId)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Get_CoachDroppedBelowThePaymentsTier_ClientStillSeesWhatWasAlreadyRequested()
    {
        var world = await CreateWorldAsync(factory);
        var rows = await StartMonthlyAsync(world);
        await PostActionAsync(world, rows[0].PaymentId, "request", new { SendEmail = true });

        await CoachSubscriptionBuilder.MoveToPlanWithoutPaymentsAsync(factory, world.Coach);

        (await GetOwnAsync(world.Client)).Coaches.Single().Requested.Should().ContainSingle();
        (await world.Coach.Http.GetAsync($"{world.Route}/payments", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "the tier gate applies to the coach, not to the client");
    }

    [Fact]
    public async Task Get_PaymentsOfTwoCoaches_AreGroupedPerCoach()
    {
        var first = await CreateWorldAsync(factory);
        var rowsFirst = await StartMonthlyAsync(first);
        await PostActionAsync(first, rowsFirst[0].PaymentId, "request", new { SendEmail = true });

        var secondCoach = await TestActors.Nutritionist(factory).WithName("Petra", "Dvořáková").CreateAsync(TestContext.Current.CancellationToken);
        await SubscribeAsync(factory, secondCoach, canUsePayments: true);
        await SetBankAccountAsync(factory, secondCoach, OtherIban, "Petra Dvořáková");
        await TestActors.Link(factory, secondCoach, first.Client).AsRole(UserRole.Nutritionist).CreateAsync(TestContext.Current.CancellationToken);
        var second = first with { Coach = secondCoach };
        var rowsSecond = await StartMonthlyAsync(second, 900m);
        await PostActionAsync(second, rowsSecond[0].PaymentId, "request", new { SendEmail = true });

        var own = await GetOwnAsync(first.Client);

        own.Coaches.Should().HaveCount(2);
        own.Coaches.Select(c => c.CoachName).Should().BeEquivalentTo("Martin Král", "Petra Dvořáková");
        own.Coaches.Single(c => c.CoachName == "Petra Dvořáková").Requested.Single().Iban.Should().Be(OtherIban);
    }
}
