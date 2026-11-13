using System.Net;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.PaymentSettings;

/// <summary>
/// Integration tests for <c>POST /payments/bank-account/code</c>.
/// </summary>
[Collection(TestCollection.Name)]
public class SendBankAccountReauthCodeEndpointTests(FitnessApiFactory factory)
{
    private FakeEmailService Emails => factory.Services.GetRequiredService<FakeEmailService>();

    private async Task<List<Application.Domain.Entities.CoachReauthCode>> ReadCodesAsync(Actor coach)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.CoachReauthCodes.AsNoTracking()
            .Where(c => c.UserId == coach.UserId)
            .OrderBy(c => c.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SendCode_NoPasswordCoach_EmailsSixDigitCodeAndStoresOnlyItsHash()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().WithLanguage("de").CreateAsync(TestContext.Current.CancellationToken);

        var response = await coach.Http.PostAsync("/payments/bank-account/code", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var sent = Emails.SentReauthCodes.Should().ContainSingle(e => e.Email == coach.Email).Subject;
        sent.Code.Should().MatchRegex("^[0-9]{6}$");
        sent.Language.Should().Be("de");

        var stored = (await ReadCodesAsync(coach)).Should().ContainSingle().Subject;
        stored.CodeHash.Should().HaveLength(64).And.NotBe(sent.Code);
        stored.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(10), TimeSpan.FromMinutes(1));
        stored.UsedAt.Should().BeNull();
    }

    [Fact]
    public async Task SendCode_AskedTwice_ReplacesTheEarlierCode()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);

        await coach.Http.PostAsync("/payments/bank-account/code", null, TestContext.Current.CancellationToken);
        await coach.Http.PostAsync("/payments/bank-account/code", null, TestContext.Current.CancellationToken);

        var codes = await ReadCodesAsync(coach);
        codes.Should().HaveCount(2);
        codes[0].UsedAt.Should().NotBeNull();
        codes[1].UsedAt.Should().BeNull();
    }

    [Fact]
    public async Task SendCode_CoachWithPassword_Returns400AndStoresAndSendsNothing()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().WithPassword(Password).CreateAsync(TestContext.Current.CancellationToken);

        var response = await coach.Http.PostAsync("/payments/bank-account/code", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodesAsync(response)).Should().Contain("REAUTH_CODE_NOT_AVAILABLE");
        (await ReadCodesAsync(coach)).Should().BeEmpty();
        Emails.SentReauthCodes.Should().NotContain(e => e.Email == coach.Email);
    }
}
