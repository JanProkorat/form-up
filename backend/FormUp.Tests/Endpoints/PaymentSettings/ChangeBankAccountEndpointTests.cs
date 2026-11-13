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
/// Integration tests for <c>PUT /payments/bank-account</c>.
/// </summary>
[Collection(TestCollection.Name)]
public class ChangeBankAccountEndpointTests(FitnessApiFactory factory)
{
    private FakeEmailService Emails => factory.Services.GetRequiredService<FakeEmailService>();

    private Task<Actor> CoachWithPasswordAsync(string? language = null)
    {
        var builder = TestActors.Trainer(factory).WithPayments().WithPassword(Password);
        return (language is null ? builder : builder.WithLanguage(language)).CreateAsync(TestContext.Current.CancellationToken);
    }

    private Task<Actor> CoachWithoutPasswordAsync() =>
        TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);

    private async Task<string> RequestCodeAsync(Actor coach)
    {
        var response = await coach.Http.PostAsync("/payments/bank-account/code", null, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        return Emails.SentReauthCodes.Last(e => e.Email == coach.Email).Code;
    }

    private Task<HttpResponseMessage> ChangeAsync(Actor coach, string iban, string? password = null, string? code = null) =>
        coach.Http.PutAsJsonAsync(
            "/payments/bank-account",
            new { Iban = iban, AccountHolder = "Martin Král", Password = password, Code = code },
            TestContext.Current.CancellationToken);

    private async Task<List<AuditLog>> ReadAuditAsync(Actor coach)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.AuditLogs.AsNoTracking()
            .Where(a => a.UserId == coach.UserId && a.Action == "ChangeBankAccount")
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Change_FirstIban_StoresNormalizedAccountAuditsMaskedAndSendsNoEmail()
    {
        var coach = await CoachWithPasswordAsync();

        var response = await ChangeAsync(coach, "cz65 0800 0000 1920 0014 5399", Password);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var settings = await ReadSettingsAsync(factory, coach);
        settings!.Iban.Should().Be(ValidIban);
        settings.AccountHolder.Should().Be("Martin Král");
        settings.BankAccountChangedAt.Should().NotBeNull();

        var change = (await ReadChangesAsync(factory, coach)).Should().ContainSingle().Subject;
        change.PreviousIban.Should().BeNull();
        change.UndoTokenHash.Should().BeNull();

        Emails.SentBankAccountChanges.Should().NotContain(e => e.Email == coach.Email);

        var audit = (await ReadAuditAsync(coach)).Should().ContainSingle().Subject;
        audit.NewValues.Should().Contain("CZ65 **** 5399").And.NotContain(ValidIban);
        audit.OldValues.Should().BeNull();
    }

    [Fact]
    public async Task Change_ExistingIban_EmailsMaskedUndoLinkAndStoresOnlyTheTokenHash()
    {
        var coach = await CoachWithPasswordAsync("cs");
        await SetBankAccountAsync(factory, coach, ValidIban);

        var response = await ChangeAsync(coach, OtherIban, Password);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadSettingsAsync(factory, coach))!.Iban.Should().Be(OtherIban);

        var sent = Emails.SentBankAccountChanges.Should().ContainSingle(e => e.Email == coach.Email).Subject;
        sent.MaskedNewIban.Should().Be("SK31 **** 7541");
        sent.MaskedPreviousIban.Should().Be("CZ65 **** 5399");
        sent.Language.Should().Be("cs");

        var change = (await ReadChangesAsync(factory, coach)).Should().ContainSingle().Subject;
        change.PreviousIban.Should().Be(ValidIban);
        change.UndoTokenHash.Should().HaveLength(64).And.NotBe(sent.UndoToken);
        change.UndoExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(7), TimeSpan.FromMinutes(5));

        var audit = (await ReadAuditAsync(coach)).Should().ContainSingle().Subject;
        audit.OldValues.Should().Contain("CZ65 **** 5399");
        audit.NewValues.Should().Contain("SK31 **** 7541");
        $"{audit.OldValues}{audit.NewValues}".Should().NotContain(ValidIban).And.NotContain(OtherIban);
    }

    [Fact]
    public async Task Change_SameIbanAndHolder_ChangesNothingAndSendsNoEmail()
    {
        var coach = await CoachWithPasswordAsync();
        await SetBankAccountAsync(factory, coach, ValidIban);

        var response = await ChangeAsync(coach, ValidIban, Password);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadChangesAsync(factory, coach)).Should().BeEmpty();
        Emails.SentBankAccountChanges.Should().NotContain(e => e.Email == coach.Email);
    }

    [Fact]
    public async Task Change_InvalidIban_Returns400WithCode()
    {
        var coach = await CoachWithPasswordAsync();

        var response = await ChangeAsync(coach, "CZ6508000000192000145398", Password);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodesAsync(response)).Should().Contain("INVALID_IBAN");
        (await ReadSettingsAsync(factory, coach)).Should().BeNull();
    }

    [Fact]
    public async Task Change_WrongPassword_Returns400AndChangesNothing()
    {
        var coach = await CoachWithPasswordAsync();
        await SetBankAccountAsync(factory, coach, ValidIban);

        var response = await ChangeAsync(coach, OtherIban, "WrongPass123!");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodesAsync(response)).Should().Contain("INVALID_CURRENT_PASSWORD");
        (await ReadSettingsAsync(factory, coach))!.Iban.Should().Be(ValidIban);
        (await ReadChangesAsync(factory, coach)).Should().BeEmpty();
        (await ReadAuditAsync(coach)).Should().BeEmpty();
        Emails.SentBankAccountChanges.Should().NotContain(e => e.Email == coach.Email);
    }

    [Fact]
    public async Task Change_PasswordUserSendsCodeInsteadOfPassword_Returns400()
    {
        var coach = await CoachWithPasswordAsync();

        var response = await ChangeAsync(coach, ValidIban, code: "123456");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodesAsync(response)).Should().Contain("REAUTH_PASSWORD_REQUIRED");
        (await ReadSettingsAsync(factory, coach)).Should().BeNull();
    }

    [Fact]
    public async Task Change_NoPasswordUserSendsPassword_Returns400()
    {
        var coach = await CoachWithoutPasswordAsync();

        var response = await ChangeAsync(coach, ValidIban, Password);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodesAsync(response)).Should().Contain("REAUTH_CODE_REQUIRED");
        (await ReadSettingsAsync(factory, coach)).Should().BeNull();
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("expired")]
    [InlineData("reused")]
    public async Task Change_WrongExpiredOrReusedCode_Returns400AndKeepsTheAccount(string scenario)
    {
        var coach = await CoachWithoutPasswordAsync();
        await SetBankAccountAsync(factory, coach, ValidIban);
        var code = await RequestCodeAsync(coach);
        var submitted = code;

        switch (scenario)
        {
            case "wrong":
                submitted = code == "000000" ? "111111" : "000000";
                break;
            case "expired":
                using (var scope = factory.Services.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    await db.CoachReauthCodes.Where(c => c.UserId == coach.UserId)
                        .ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)), TestContext.Current.CancellationToken);
                }

                break;
            case "reused":
                // The first use of the emailed code succeeds; only the second is refused.
                (await ChangeAsync(coach, OtherIban, code: code)).StatusCode.Should().Be(HttpStatusCode.OK);
                break;
        }

        var response = await ChangeAsync(coach, ThirdIban, code: submitted);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodesAsync(response)).Should().Contain("REAUTH_CODE_INVALID");
        (await ReadSettingsAsync(factory, coach))!.Iban.Should().Be(scenario == "reused" ? OtherIban : ValidIban);
    }

    [Fact]
    public async Task Change_RightCodeAfterFiveWrongAttempts_Returns400BecauseTheCodeIsLocked()
    {
        var coach = await CoachWithoutPasswordAsync();
        var code = await RequestCodeAsync(coach);
        var wrong = code == "000000" ? "111111" : "000000";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            (await ChangeAsync(coach, ValidIban, code: wrong)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        var response = await ChangeAsync(coach, ValidIban, code: code);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodesAsync(response)).Should().Contain("REAUTH_CODE_INVALID");
        (await ReadSettingsAsync(factory, coach)).Should().BeNull();
    }
}
