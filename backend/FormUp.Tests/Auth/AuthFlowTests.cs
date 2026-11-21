using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Auth;

/// <summary>
/// Integration tests for the complete authentication flow:
/// register, login, refresh token, logout, password reset.
/// </summary>
[Collection(TestCollection.Shard2)]
public class AuthFlowTests(FitnessApiFactory factory)
{
    // Per-host singleton (#726 refinement) — resolved from this factory's own DI
    // container so assertions never see another factory's zombie worker traffic.
    private FakeEmailService EmailService => factory.Services.GetRequiredService<FakeEmailService>();

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@test.com";

    [Fact]
    public async Task Register_WithValidData_Returns201()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        var response = await TestHelpers.RegisterAsync(
            client, email, "TestPass1!", "John", "Doe", "Client");

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<RegisterResult>(cancellationToken: TestContext.Current.CancellationToken);
        body!.Email.Should().Be(email);
        body.Message.Should().Be("Registration successful.");
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_Returns400()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "John", "Doe", "Client");

        var response = await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Jane", "Doe", "Client");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_WithoutGdprConsent_Returns400()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = UniqueEmail(),
            Password = "TestPass1!",
            ConfirmPassword = "TestPass1!",
            FirstName = "John",
            LastName = "Doe",
            Roles = new[] { "Client" },
            GdprConsent = false
        }, cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsTokens()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "John", "Doe", "Client");

        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            Email = email,
            Password = "TestPass1!"
        }, cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<LoginResult>(cancellationToken: TestContext.Current.CancellationToken);
        body!.AccessToken.Should().NotBeNullOrEmpty();
        body.RefreshToken.Should().NotBeNullOrEmpty();
        body.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task Login_WithInvalidPassword_Returns400()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "John", "Doe", "Client");

        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            Email = email,
            Password = "WrongPassword1!"
        }, cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RefreshToken_WithValidToken_ReturnsNewTokens()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "John", "Doe", "Client");

        var (_, refreshToken) = await TestHelpers.LoginAsync(client, email, "TestPass1!");

        var response = await client.PostAsJsonAsync("/auth/refresh", new
        {
            RefreshToken = refreshToken
        }, cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<LoginResult>(cancellationToken: TestContext.Current.CancellationToken);
        body!.AccessToken.Should().NotBeNullOrEmpty();
        body.RefreshToken.Should().NotBe(refreshToken);
    }

    /// <summary>
    /// Immediate reuse of a just-rotated refresh token (well within #652's
    /// grace window — default 20s) is the legitimate benign-reconcile path,
    /// NOT theft: the second call must succeed and hand back the SAME
    /// successor token the first call minted, rather than rejecting outright.
    /// Reuse detection for a token replayed OUTSIDE the grace window (the
    /// actual theft path) is covered against real Postgres by
    /// <c>RefreshTokenReuseDetectionConcurrencyTests.ReplayOutsideGraceWindow_RevokesWholeFamily_AndRejectsBothTokens</c>.
    /// </summary>
    [Fact]
    public async Task RefreshToken_UsedTwiceImmediately_ReconcilesBenignlyWithSameSuccessor()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "John", "Doe", "Client");

        var (_, refreshToken) = await TestHelpers.LoginAsync(client, email, "TestPass1!");

        var first = await client.PostAsJsonAsync("/auth/refresh", new { RefreshToken = refreshToken }, cancellationToken: TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await first.Content.ReadFromJsonAsync<LoginResult>(cancellationToken: TestContext.Current.CancellationToken);

        var second = await client.PostAsJsonAsync("/auth/refresh", new { RefreshToken = refreshToken }, cancellationToken: TestContext.Current.CancellationToken);
        second.StatusCode.Should().Be(HttpStatusCode.OK,
            "immediate reuse within the grace window is a benign reconcile, not theft");

        var secondBody = await second.Content.ReadFromJsonAsync<LoginResult>(cancellationToken: TestContext.Current.CancellationToken);
        secondBody!.RefreshToken.Should().Be(firstBody!.RefreshToken,
            "the reconcile must hand back the SAME successor the first call minted");
    }

    [Fact]
    public async Task Logout_RevokesRefreshToken()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "John", "Doe", "Client");

        var (accessToken, refreshToken) = await TestHelpers.LoginAsync(client, email, "TestPass1!");

        TestHelpers.SetBearerToken(client, accessToken);

        var logoutResponse = await client.PostAsJsonAsync("/auth/logout", new
        {
            RefreshToken = refreshToken
        }, cancellationToken: TestContext.Current.CancellationToken);

        logoutResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var refreshResponse = await client.PostAsJsonAsync("/auth/refresh", new
        {
            RefreshToken = refreshToken
        }, cancellationToken: TestContext.Current.CancellationToken);

        refreshResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetProfile_WithValidToken_ReturnsProfile()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "John", "Doe", "Client");

        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");

        TestHelpers.SetBearerToken(client, accessToken);

        var response = await client.GetAsync("/users/me", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ProfileResult>(cancellationToken: TestContext.Current.CancellationToken);
        body!.Email.Should().Be(email);
        body.FirstName.Should().Be("John");
        body.LastName.Should().Be("Doe");
    }

    [Fact]
    public async Task PasswordReset_FullFlow_Works()
    {
        EmailService.Reset();
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "John", "Doe", "Client");

        var requestResponse = await client.PostAsJsonAsync("/auth/password/reset", new { Email = email }, cancellationToken: TestContext.Current.CancellationToken);

        requestResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        EmailService.SentPasswordResets.Should().ContainSingle();

        var resetToken = EmailService.SentPasswordResets[0].Token;

        var resetResponse = await client.PutAsJsonAsync("/auth/password/reset", new
            {
                Token = resetToken,
                Email = email,
                NewPassword = "NewTestPass1!",
                ConfirmPassword = "NewTestPass1!"
            }, cancellationToken: TestContext.Current.CancellationToken);

        resetResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "NewTestPass1!");

        accessToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PasswordReset_Success_RevokesOwnRefreshTokensOnly()
    {
        EmailService.Reset();
        var client = factory.CreateClient();
        var emailA = UniqueEmail();
        var emailB = UniqueEmail();

        await TestHelpers.RegisterAsync(client, emailA, "TestPass1!", "Anna", "Doe", "Client");
        await TestHelpers.RegisterAsync(client, emailB, "TestPass1!", "Ben", "Doe", "Client");

        var (_, refreshTokenA) = await TestHelpers.LoginAsync(client, emailA, "TestPass1!");
        var (_, refreshTokenB) = await TestHelpers.LoginAsync(client, emailB, "TestPass1!");

        await client.PostAsJsonAsync("/auth/password/reset", new { Email = emailA },
            cancellationToken: TestContext.Current.CancellationToken);
        var resetToken = EmailService.SentPasswordResets.Single(r => r.Email == emailA).Token;

        var resetResponse = await client.PutAsJsonAsync("/auth/password/reset", new
        {
            Token = resetToken,
            Email = emailA,
            NewPassword = "NewTestPass1!",
            ConfirmPassword = "NewTestPass1!"
        }, cancellationToken: TestContext.Current.CancellationToken);
        resetResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var refreshA = await client.PostAsJsonAsync("/auth/refresh", new { RefreshToken = refreshTokenA },
            cancellationToken: TestContext.Current.CancellationToken);
        refreshA.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var refreshB = await client.PostAsJsonAsync("/auth/refresh", new { RefreshToken = refreshTokenB },
            cancellationToken: TestContext.Current.CancellationToken);
        refreshB.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PasswordReset_NonExistentEmail_Returns404WithErrorCode()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/password/reset", new
        {
            Email = "nonexistent-" + UniqueEmail()
        }, cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(
            TestContext.Current.CancellationToken);
        body.GetProperty("errorCode").GetString().Should().Be("EMAIL_NOT_REGISTERED");
    }

    // --- Art. 9 health-data consent integration tests ---

    [Fact]
    public async Task Register_CoachHappyPath_Returns201_AndHealthDataConsentIsNull()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        var response = await TestHelpers.RegisterAsync(
            client, email, "TestPass1!", "Jane", "Trainer",
            new[] { "Trainer" }, gdprConsent: true, healthDataConsent: null);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        // Verify DB: HealthDataConsent and HealthDataConsentDate must be null
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, TestContext.Current.CancellationToken);
        user.Should().NotBeNull();
        user!.HealthDataConsent.Should().BeNull();
        user.HealthDataConsentDate.Should().BeNull();
        user.GdprConsentDate.Should().NotBeNull();
    }

    [Fact]
    public async Task Register_ClientHappyPath_Returns201_AndBothConsentFieldsPersisted()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();
        var before = DateTime.UtcNow;

        var response = await TestHelpers.RegisterAsync(
            client, email, "TestPass1!", "John", "Client",
            new[] { "Client" }, gdprConsent: true, healthDataConsent: true);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var after = DateTime.UtcNow;

        // Verify DB: both consent flags true, both timestamps set within the request window
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, TestContext.Current.CancellationToken);
        user.Should().NotBeNull();
        user!.HealthDataConsent.Should().BeTrue();
        user.HealthDataConsentDate.Should().NotBeNull();
        user.HealthDataConsentDate!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after.AddSeconds(5));
        user.GdprConsentDate.Should().NotBeNull();
        user.GdprConsentDate!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after.AddSeconds(5));
    }

    [Fact]
    public async Task Register_CoachWithHealthDataConsent_Returns400()
    {
        var client = factory.CreateClient();

        var response = await TestHelpers.RegisterAsync(
            client, UniqueEmail(), "TestPass1!", "Jane", "Trainer",
            new[] { "Trainer" }, gdprConsent: true, healthDataConsent: true);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_ClientWithoutHealthDataConsent_Returns400()
    {
        var client = factory.CreateClient();

        var response = await TestHelpers.RegisterAsync(
            client, UniqueEmail(), "TestPass1!", "John", "Client",
            new[] { "Client" }, gdprConsent: true, healthDataConsent: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private record RegisterResult(Guid UserId, string Email, string Message);
    private record LoginResult(string AccessToken, string RefreshToken, DateTime ExpiresAt);
    private record ProfileResult(Guid UserId, string Email, string FirstName, string LastName, List<string> Roles);
}
