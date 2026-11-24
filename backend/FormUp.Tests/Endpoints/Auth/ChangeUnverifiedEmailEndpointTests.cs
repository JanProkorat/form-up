using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Features.Auth.ChangeUnverifiedEmail;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Auth;

/// <summary>
/// Integration tests for <see cref="ChangeUnverifiedEmailEndpoint"/>.
/// </summary>
[Collection(TestCollection.Shard4)]
public class ChangeUnverifiedEmailEndpointTests(FitnessApiFactory factory)
{
    private const string Route = "/auth/email";

    private FakeEmailService EmailService => factory.Services.GetRequiredService<FakeEmailService>();

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@change-email-test.com";

    private async Task<ApplicationUser> LoadUserAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
    }

    private async Task SetVerificationEmailsSentAsync(Guid userId, int sent)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        user.VerificationEmailsSent = sent;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Change_UnverifiedClient_UpdatesEmail_InvalidatesOldToken_AndSendsNewOne()
    {
        var actor = await TestActors.Client(factory).Unverified().CreateAsync(TestContext.Current.CancellationToken);
        const string oldToken = "OLD-TOKEN-FOR-CHANGE-EMAIL-TEST";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.EmailVerificationTokens.Add(new EmailVerificationToken
            {
                UserId = actor.UserId,
                Token = oldToken,
                ExpiresAt = DateTime.UtcNow.AddHours(1),
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var newEmail = UniqueEmail();

        var response = await actor.Http.PutAsJsonAsync(
            Route, new { NewEmail = newEmail }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ChangeUnverifiedEmailResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        body!.Email.Should().Be(newEmail);
        body.RemainingResends.Should().Be(3);

        var user = await LoadUserAsync(actor.UserId);
        user.Email.Should().Be(newEmail);
        user.UserName.Should().Be(newEmail);
        user.NormalizedEmail.Should().Be(newEmail.ToUpperInvariant());
        user.EmailConfirmed.Should().BeFalse();
        user.VerificationEmailsSent.Should().Be(1);

        var newToken = EmailService.SentVerifications.Single(v => v.Email == newEmail).Token;

        var anonymous = factory.CreateClient();
        var oldResponse = await anonymous.PostAsJsonAsync(
            "/auth/verify-email", new { Token = oldToken }, TestContext.Current.CancellationToken);
        oldResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var newResponse = await anonymous.PostAsJsonAsync(
            "/auth/verify-email", new { Token = newToken }, TestContext.Current.CancellationToken);
        newResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var auditScope = factory.Services.CreateScope();
        var auditDb = auditScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audit = await auditDb.AuditLogs.AsNoTracking().SingleAsync(
            a => a.UserId == actor.UserId && a.Action == "ChangeUnverifiedEmail",
            TestContext.Current.CancellationToken);
        audit.OldValues.Should().Contain(actor.Email);
        audit.NewValues.Should().Contain(newEmail);
    }

    [Fact]
    public async Task Change_VerifiedAccount_Returns400_EmailAlreadyVerified()
    {
        var actor = await TestActors.Client(factory).Verified().CreateAsync(TestContext.Current.CancellationToken);

        var response = await actor.Http.PutAsJsonAsync(
            Route, new { NewEmail = UniqueEmail() }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(ErrorCodes.EmailAlreadyVerified);
    }

    [Fact]
    public async Task Change_AddressOfAnotherAccount_Returns400_EmailAlreadyInUse_AndKeepsEmail()
    {
        var actor = await TestActors.Client(factory).Unverified().CreateAsync(TestContext.Current.CancellationToken);
        var other = await TestActors.Client(factory).Unverified().CreateAsync(TestContext.Current.CancellationToken);

        var response = await actor.Http.PutAsJsonAsync(
            Route, new { NewEmail = other.Email.ToUpperInvariant() }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(ErrorCodes.EmailAlreadyInUse);

        (await LoadUserAsync(actor.UserId)).Email.Should().Be(actor.Email);
        EmailService.SentVerifications.Should().NotContain(v => v.Email == other.Email);
    }

    [Fact]
    public async Task Change_SameAddressInOtherCase_Returns400_EmailUnchanged()
    {
        var actor = await TestActors.Client(factory).Unverified().CreateAsync(TestContext.Current.CancellationToken);

        var response = await actor.Http.PutAsJsonAsync(
            Route, new { NewEmail = actor.Email.ToUpperInvariant() }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(ErrorCodes.EmailUnchanged);
    }

    [Fact]
    public async Task Change_LifetimeCapReached_Returns400_AndKeepsEmail_NoMailSent()
    {
        var actor = await TestActors.Client(factory).Unverified().CreateAsync(TestContext.Current.CancellationToken);
        await SetVerificationEmailsSentAsync(actor.UserId, 4);
        var newEmail = UniqueEmail();

        var response = await actor.Http.PutAsJsonAsync(
            Route, new { NewEmail = newEmail }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(ErrorCodes.VerificationResendLimitReached);

        (await LoadUserAsync(actor.UserId)).Email.Should().Be(actor.Email);
        EmailService.SentVerifications.Should().NotContain(v => v.Email == newEmail);
    }

    [Fact]
    public async Task Change_InvalidAddress_Returns400()
    {
        var actor = await TestActors.Client(factory).Unverified().CreateAsync(TestContext.Current.CancellationToken);

        var response = await actor.Http.PutAsJsonAsync(
            Route, new { NewEmail = "not-an-email" }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
