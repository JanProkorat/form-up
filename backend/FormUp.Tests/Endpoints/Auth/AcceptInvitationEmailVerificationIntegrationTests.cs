using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Auth;

/// <summary>
/// POST /auth/invite/accept must not verify the caller's email (#1515). Since #1517 the endpoint is behind the
/// email-verification gate, so an unverified caller is refused and accepts only after verifying.
/// </summary>
[Collection(TestCollection.Name)]
public class AcceptInvitationEmailVerificationIntegrationTests(FitnessApiFactory factory)
{
    private const string Password = "TestPass1!";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeEmailService EmailService => factory.Services.GetRequiredService<FakeEmailService>();

    private async Task<string> SeedTokenAsync(Actor coach, string email)
    {
        var token = Guid.NewGuid().ToString("N");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.InvitationTokens.Add(new InvitationToken
        {
            ProfessionalProfileId = coach.ProfileId,
            Email = email,
            Token = token,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
        });
        await db.SaveChangesAsync(Ct);

        return token;
    }

    private async Task<bool> IsEmailConfirmedAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Users.Where(u => u.Id == userId).Select(u => u.EmailConfirmed).SingleAsync(Ct);
    }

    [Fact]
    public async Task Accept_AfterInviteRegistration_IsRefusedUntilVerified_ThenAcceptSucceedsWithoutAnotherEmail()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var email = $"{Guid.NewGuid():N}@invite-verify.com";
        var inviteToken = await SeedTokenAsync(coach, email);

        var anonymous = factory.CreateClient();
        var registerResponse = await anonymous.PostAsJsonAsync("/auth/register", new
        {
            Email = email,
            Password = Password,
            ConfirmPassword = Password,
            FirstName = "Invited",
            LastName = "Client",
            Roles = new[] { "Client" },
            GdprConsent = true,
            HealthDataConsent = true,
            InviteToken = inviteToken,
        }, Ct);
        registerResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var (accessToken, _) = await TestHelpers.LoginAsync(anonymous, email, Password);
        var authed = factory.CreateClient();
        TestHelpers.SetBearerToken(authed, accessToken);

        var refused = await authed.PostAsJsonAsync("/auth/invite/accept", new { Token = inviteToken }, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.EmailNotVerified);

        Guid userId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(Ct);
        }

        (await IsEmailConfirmedAsync(userId)).Should().BeFalse();

        var sent = EmailService.SentVerifications.Where(v => v.Email == email).ToList();
        sent.Should().ContainSingle("register issued the only verification email; the refused accept sends none");

        var verify = await anonymous.PostAsJsonAsync("/auth/verify-email", new { Token = sent[0].Token }, Ct);

        verify.StatusCode.Should().Be(HttpStatusCode.OK);
        (await IsEmailConfirmedAsync(userId)).Should().BeTrue();

        var accept = await authed.PostAsJsonAsync("/auth/invite/accept", new { Token = inviteToken }, Ct);

        accept.StatusCode.Should().Be(HttpStatusCode.OK);
        EmailService.SentVerifications.Where(v => v.Email == email).Should().ContainSingle();
    }

    [Fact]
    public async Task Accept_UnverifiedCaller_Returns403_AndCreatesNoLinkOrEmail()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var caller = await TestActors.Client(factory).Unverified().CreateAsync(Ct);
        var inviteToken = await SeedTokenAsync(coach, caller.Email);

        var accept = await caller.Http.PostAsJsonAsync("/auth/invite/accept", new { Token = inviteToken }, Ct);

        accept.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await accept.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.EmailNotVerified);
        (await IsEmailConfirmedAsync(caller.UserId)).Should().BeFalse();
        EmailService.SentVerifications.Where(v => v.Email == caller.Email).Should().BeEmpty();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.ClientProfessionalLinks.AnyAsync(
            l => l.ClientProfileId == caller.ProfileId && l.ProfessionalProfileId == coach.ProfileId, Ct)).Should().BeFalse();
        (await db.InvitationTokens.SingleAsync(t => t.Token == inviteToken, Ct)).IsUsed.Should().BeFalse();
    }

    [Fact]
    public async Task Accept_ConfirmedCaller_StaysConfirmed_AndSendsNoEmail()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var caller = await TestActors.Client(factory).Verified().CreateAsync(Ct);
        var inviteToken = await SeedTokenAsync(coach, caller.Email);

        var accept = await caller.Http.PostAsJsonAsync("/auth/invite/accept", new { Token = inviteToken }, Ct);

        accept.StatusCode.Should().Be(HttpStatusCode.OK);
        (await IsEmailConfirmedAsync(caller.UserId)).Should().BeTrue();
        EmailService.SentVerifications.Where(v => v.Email == caller.Email).Should().BeEmpty();
    }
}
