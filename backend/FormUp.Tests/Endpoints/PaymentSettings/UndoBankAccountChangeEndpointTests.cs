using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.PaymentSettings;

/// <summary>
/// Integration tests for <c>POST /payments/bank-account/undo</c>.
/// </summary>
[Collection(TestCollection.Name)]
public class UndoBankAccountChangeEndpointTests(FitnessApiFactory factory)
{
    private FakeEmailService Emails => factory.Services.GetRequiredService<FakeEmailService>();

    /// <summary>
    /// A coach whose account went ValidIban -> OtherIban through the real endpoint; returns the
    /// coach and the undo token that was emailed.
    /// </summary>
    private async Task<(Actor Coach, string Token)> CoachWhoChangedAccountAsync()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().WithPassword(Password).CreateAsync(TestContext.Current.CancellationToken);
        await SetBankAccountAsync(factory, coach, ValidIban, "Old Holder");

        var change = await ChangeAsync(coach, OtherIban);
        change.StatusCode.Should().Be(HttpStatusCode.OK);

        return (coach, Emails.SentBankAccountChanges.Single(e => e.Email == coach.Email).UndoToken);
    }

    private Task<HttpResponseMessage> ChangeAsync(Actor coach, string iban) =>
        coach.Http.PutAsJsonAsync(
            "/payments/bank-account",
            new { Iban = iban, AccountHolder = "New Holder", Password },
            TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> UndoAsync(string token) =>
        factory.CreateClient().PostAsJsonAsync("/payments/bank-account/undo", new { Token = token }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Undo_ValidToken_RestoresPreviousAccountAndTheTokenWorksOnce()
    {
        var (coach, token) = await CoachWhoChangedAccountAsync();

        var response = await UndoAsync(token);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var settings = await ReadSettingsAsync(factory, coach);
        settings!.Iban.Should().Be(ValidIban);
        settings.AccountHolder.Should().Be("Old Holder");

        var history = await ReadChangesAsync(factory, coach);
        history.Should().HaveCount(2);
        history[0].UndoneAt.Should().NotBeNull();
        history[1].Should().Match<CoachBankAccountChange>(c =>
            c.PreviousIban == OtherIban
            && c.NewIban == ValidIban
            && c.NewAccountHolder == "Old Holder"
            && c.UndoTokenHash == null
            && c.UndoneAt == null);

        var second = await UndoAsync(token);

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodesAsync(second)).Should().Contain("BANK_ACCOUNT_UNDO_INVALID");
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("expired")]
    public async Task Undo_UnknownOrExpiredToken_Returns400AndKeepsTheAccount(string scenario)
    {
        var (coach, token) = await CoachWhoChangedAccountAsync();

        if (scenario == "unknown")
        {
            token = "not-a-real-token";
        }
        else
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CoachBankAccountChanges.Where(c => c.ProfessionalProfileId == coach.ProfileId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UndoExpiresAt, DateTime.UtcNow.AddMinutes(-1)), TestContext.Current.CancellationToken);
        }

        var response = await UndoAsync(token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodesAsync(response)).Should().Contain("BANK_ACCOUNT_UNDO_INVALID");
        (await ReadSettingsAsync(factory, coach))!.Iban.Should().Be(OtherIban);
    }

    [Fact]
    public async Task Undo_AfterALaterChange_Returns409AndRestoresNothing()
    {
        var (coach, token) = await CoachWhoChangedAccountAsync();
        (await ChangeAsync(coach, ThirdIban)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await UndoAsync(token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(response)).Should().Be("BANK_ACCOUNT_UNDO_SUPERSEDED");
        (await ReadSettingsAsync(factory, coach))!.Iban.Should().Be(ThirdIban);
        (await ReadChangesAsync(factory, coach)).Should().OnlyContain(c => c.UndoneAt == null);
    }
}
