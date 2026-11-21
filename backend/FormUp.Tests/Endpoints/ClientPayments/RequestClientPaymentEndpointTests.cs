using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.ClientPayments.RequestClientPayment;
using FormUp.Application.Features.ClientPayments.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FormUp.Tests.Endpoints.ClientPayments.ClientPaymentFixtures;
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.ClientPayments;

/// <summary>
/// Integration tests for POST /payments/clients/{ClientId}/payments/{PaymentId}/request.
/// </summary>
[Collection(TestCollection.Shard3)]
public class RequestClientPaymentEndpointTests(FitnessApiFactory factory)
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private async Task<int> CountNotificationsAsync(Guid recipientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Notifications.CountAsync(
            n => n.RecipientUserId == recipientUserId && n.Type == NotificationType.PaymentRequested,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Request_UpcomingPayment_FreezesItSnapshotsTheAccountAndSendsTheQrEmailAndNotification()
    {
        var world = await CreateWorldAsync(factory);
        var discountId = await CreateDiscountAsync(world.Coach, 30m);
        var rows = await StartMonthlyAsync(world, 2500m, discountId);
        var upcoming = rows[0];

        var response = await PostActionAsync(world, upcoming.PaymentId, "request", new { SendEmail = true, SendNotification = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadAsync<RequestClientPaymentResponse>(response);
        body.EmailSent.Should().BeTrue();
        body.NotificationSent.Should().BeTrue();
        body.Payment.Status.Should().Be(ClientPaymentStatus.Requested);
        body.Payment.Amount.Should().Be(1750m);
        body.Payment.DueDate.Should().Be(Today.AddDays(7), "the period started today and the default term is seven days");
        body.Payment.VariableSymbol.Should().Be(upcoming.VariableSymbol);
        body.Payment.Message.Should().Be(upcoming.Message);
        body.Payment.RequestedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        (await CountNotificationsAsync(world.Client.UserId)).Should().Be(1);

        var sent = Email(factory).SentPaymentRequests.Single(e => e.ToEmail == world.Client.Email);
        sent.Amount.Should().Be(1750m);
        sent.CoachName.Should().Be("Martin Král");
        sent.Language.Should().Be("cs");
        sent.Iban.Should().Be(ValidIban);
        sent.VariableSymbol.Should().Be(upcoming.VariableSymbol);
        sent.QrContentId.Should().Be(PaymentMessageComposer.QrContentId);
        sent.QrPng.Take(8).Should().Equal(PngSignature);
        sent.DiscountLabel.Should().Contain("30");

        // The request snapshots the account, so a later bank account change leaves it alone.
        await SetBankAccountAsync(factory, world.Coach, OtherIban, "Someone Else");

        var stored = await ReadStoredAsync(factory, upcoming.PaymentId);
        stored.AccountIban.Should().Be(ValidIban);
        stored.AccountHolder.Should().Be("Martin Král");
        stored.BasePrice.Should().Be(2500m);
        stored.DiscountLabel.Should().Contain("Friends & family");
    }

    [Fact]
    public async Task Request_AmountOverrideAboveTheCurrencyCap_Returns400AmountInvalid()
    {
        var world = await CreateWorldAsync(factory);
        var packageId = await CreatePackageAsync(world.Coach, 100m, currency: "EUR");
        await SetTermsOkAsync(world, packageId);
        var rows = await ListAsync(world);

        var response = await PostActionAsync(
            world, rows[0].PaymentId, "request", new { SendEmail = true, Amount = 50_000.01m });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodesAsync(response)).Should().Contain("PAYMENT_AMOUNT_INVALID");
    }

    [Fact]
    public async Task AddOneOff_AmountAboveTheCurrencyCap_Returns400AmountInvalid()
    {
        var world = await CreateWorldAsync(factory);

        var response = await world.Coach.Http.PostAsJsonAsync(
            $"{world.Route}/one-off",
            new { Title = "Workshop", Amount = 50_000.01m, Currency = "EUR", DueDate = Today.AddDays(5) },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodesAsync(response)).Should().Contain("PAYMENT_AMOUNT_INVALID");
    }

    [Fact]
    public async Task Request_ByEmailOnlyWithOverrides_UsesThemEverywhereAndCreatesNoNotification()
    {
        var world = await CreateWorldAsync(factory);
        var rows = await StartMonthlyAsync(world, 2500m);
        var due = Today.AddDays(30);

        var body = await ReadAsync<RequestClientPaymentResponse>(await PostActionAsync(
            world, rows[0].PaymentId, "request", new { SendEmail = true, DueDate = due, Amount = 999.5m }));

        body.Payment.DueDate.Should().Be(due);
        body.Payment.Amount.Should().Be(999.5m);
        body.EmailSent.Should().BeTrue();
        body.NotificationSent.Should().BeFalse();
        Email(factory).SentPaymentRequests.Single(e => e.ToEmail == world.Client.Email).Amount.Should().Be(999.5m);
        (await CountNotificationsAsync(world.Client.UserId)).Should().Be(0);
    }

    [Fact]
    public async Task Request_NeitherChannel_Returns400AndChangesNothing()
    {
        var world = await CreateWorldAsync(factory);
        var rows = await StartMonthlyAsync(world);

        var response = await PostActionAsync(world, rows[0].PaymentId, "request", new { SendEmail = false, SendNotification = false });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodesAsync(response)).Should().Contain("PAYMENT_REQUEST_CHANNEL_REQUIRED");
        (await ReadStoredAsync(factory, rows[0].PaymentId)).Status.Should().Be(ClientPaymentStatus.Upcoming);
    }

    [Fact]
    public async Task Request_FullDiscount_Returns400BecauseThereIsNothingToPay()
    {
        var world = await CreateWorldAsync(factory);
        var discountId = await CreateDiscountAsync(world.Coach, 100m);
        var rows = await StartMonthlyAsync(world, 2500m, discountId);

        var response = await PostActionAsync(world, rows[0].PaymentId, "request", new { SendEmail = true });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodesAsync(response)).Should().Contain("PAYMENT_AMOUNT_INVALID");
    }

    [Fact]
    public async Task Request_CoachWithoutABankAccount_Returns409BankAccountNotSet()
    {
        var world = await CreateWorldAsync(factory, bankAccount: false);
        var rows = await StartMonthlyAsync(world);

        var response = await PostActionAsync(world, rows[0].PaymentId, "request", new { SendEmail = true });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(response)).Should().Be("BANK_ACCOUNT_NOT_SET");
        (await ReadStoredAsync(factory, rows[0].PaymentId)).Status.Should().Be(ClientPaymentStatus.Upcoming);
    }

    [Fact]
    public async Task Request_SamePaymentAgainOrTooManyForTheClient_Returns429AndSendsNothingMore()
    {
        var world = await CreateWorldAsync(factory);
        var rows = await StartMonthlyAsync(world);

        var first = await PostActionAsync(world, rows[0].PaymentId, "request", new { SendEmail = true });
        var again = await PostActionAsync(world, rows[0].PaymentId, "request", new { SendEmail = true });

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        again.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ProblemCodeAsync(again)).Should().Be("PAYMENT_REQUEST_RATE_LIMITED");

        // Ten requests for this client in the last hour block the next one, whichever payment it is for.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var assignmentId = await db.ClientPaymentAssignments
                .Where(a => a.ClientProfileId == world.Client.ProfileId)
                .Select(a => a.PublicId)
                .FirstAsync(TestContext.Current.CancellationToken);
            db.AuditLogs.AddRange(Enumerable.Range(0, 10).Select(_ => new AuditLog
            {
                UserId = world.Coach.UserId,
                Action = "RequestClientPayment",
                EntityType = "ClientPaymentAssignment",
                EntityId = assignmentId,
                DateCreated = DateTime.UtcNow.AddMinutes(-5)
            }));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var overClientLimit = await PostActionAsync(world, rows[1].PaymentId, "request", new { SendEmail = true });

        overClientLimit.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ReadStoredAsync(factory, rows[1].PaymentId)).Status.Should().Be(ClientPaymentStatus.Upcoming);
        Email(factory).SentPaymentRequests.Count(e => e.ToEmail == world.Client.Email).Should().Be(1);
    }

    [Fact]
    public async Task Request_EmailProviderFails_StillReturns200WithEmailSentFalseAndKeepsTheRequest()
    {
        var world = await CreateWorldAsync(factory);
        var rows = await StartMonthlyAsync(world);
        Email(factory).FailPaymentRequestsFor(world.Client.Email);

        var response = await PostActionAsync(world, rows[0].PaymentId, "request", new { SendEmail = true, SendNotification = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadAsync<RequestClientPaymentResponse>(response);
        body.EmailSent.Should().BeFalse();
        body.NotificationSent.Should().BeTrue();
        body.Payment.Status.Should().Be(ClientPaymentStatus.Requested);
        (await ReadStoredAsync(factory, rows[0].PaymentId)).Status.Should().Be(ClientPaymentStatus.Requested);
    }

    [Fact]
    public async Task Request_FormatSwitchedBeforeTheRequest_UsesTheCurrentSymbolFormat()
    {
        var world = await CreateWorldAsync(factory);

        // Another client of the coach takes number 1 and then waives their payment, freeing symbol ...0001.
        var firstClient = world with { Client = await AddClientAsync(world) };
        var firstRows = await StartMonthlyAsync(firstClient);
        await PostActionAsync(firstClient, firstRows[0].PaymentId, "waive");

        var rows = await StartMonthlyAsync(world);
        rows[0].VariableSymbol.Should().EndWith("0002");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var settings = await db.CoachPaymentSettings.FirstAsync(
                s => s.ProfessionalProfileId == world.Coach.ProfileId, TestContext.Current.CancellationToken);
            settings.VariableSymbolFormat = VariableSymbolFormat.YearMonthSequence;
            settings.MessageFormat = PaymentMessageFormat.ClientNameAndMonth;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Requested by notification only, so no email goes out.
        var body = await ReadAsync<RequestClientPaymentResponse>(
            await PostActionAsync(world, rows[0].PaymentId, "request", new { SendNotification = true }));

        body.EmailSent.Should().BeFalse();
        body.NotificationSent.Should().BeTrue();
        Email(factory).SentPaymentRequests.Should().NotContain(e => e.ToEmail == world.Client.Email);
        body.Payment.VariableSymbol.Should().Be($"{rows[0].PeriodStart!.Value.Year}{rows[0].PeriodStart!.Value.Month:D2}0001");
        body.Payment.Message.Should().Be($"Eva Svobodova {rows[0].PeriodStart!.Value.Month:D2}/{rows[0].PeriodStart!.Value.Year}");
    }

    [Fact]
    public async Task Request_OneOffPayment_UsesItsOwnTitleAndDueDate()
    {
        var world = await CreateWorldAsync(factory);
        var due = Today.AddDays(20);
        var created = await ReadAsync<ClientPaymentRowDto>(await world.Coach.Http.PostAsJsonAsync(
            $"{world.Route}/one-off", new { Title = "Workshop", Amount = 300m, DueDate = due }, TestContext.Current.CancellationToken));

        var body = await ReadAsync<RequestClientPaymentResponse>(
            await PostActionAsync(world, created.PaymentId, "request", new { SendEmail = true }));

        body.Payment.Amount.Should().Be(300m);
        body.Payment.DueDate.Should().Be(due);
        body.Payment.Message.Should().StartWith("Workshop");
        Email(factory).SentPaymentRequests.Last(e => e.ToEmail == world.Client.Email).PackageName.Should().Be("Workshop");
    }
}
