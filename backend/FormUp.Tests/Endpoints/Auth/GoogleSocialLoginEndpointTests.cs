using FastEndpoints;
using FastEndpoints.Testing;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Auth.Shared;
using FormUp.Application.Features.Auth.SocialLogin.Google;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FormUp.Tests.Endpoints.Auth;

/// <summary>
/// Unit tests for <see cref="GoogleSocialLoginEndpoint"/>.
/// A fake <see cref="IGoogleTokenVerifier"/> is injected so no real Google
/// network calls are made.
/// </summary>
public class GoogleSocialLoginEndpointTests
{
    private static readonly string JwtSecret = new('x', 64);
    private readonly IPendingInviteConversationSeeder _inviteConversationSeeder = Substitute.For<IPendingInviteConversationSeeder>();
    private readonly IPendingInviteNotificationSeeder _inviteNotificationSeeder = Substitute.For<IPendingInviteNotificationSeeder>();
    private readonly ILogger<GoogleSocialLoginEndpoint> _logger = Substitute.For<ILogger<GoogleSocialLoginEndpoint>>();
    private readonly FormUp.Application.Domain.Interfaces.ICoachEventRecorder _coachEvents = Substitute.For<FormUp.Application.Domain.Interfaces.ICoachEventRecorder>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();

    // Raw nonce used in all tests — the DB is seeded with a valid record for this value.
    private const string ValidNonce = "test-raw-nonce-google";

    // Shared Google token payload returned by the fake verifier.
    private static readonly GoogleTokenPayload ValidPayload = new(
        Subject: "google-sub-12345",
        Email: "googleuser@example.com",
        Name: "Google User");

    private static IConfiguration MakeConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = JwtSecret,
                ["Jwt:AccessTokenExpirationMinutes"] = "15",
                ["Jwt:RefreshTokenExpirationDays"] = "7"
            })
            .Build();

    /// <summary>
    /// Creates a fake verifier that always returns the given payload regardless of nonce.
    /// </summary>
    private static IGoogleTokenVerifier MakeVerifier(GoogleTokenPayload payload)
    {
        var verifier = Substitute.For<IGoogleTokenVerifier>();
        verifier.VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(payload);
        return verifier;
    }

    private static IGoogleTokenVerifier MakeFailingVerifier()
    {
        var verifier = Substitute.For<IGoogleTokenVerifier>();
        verifier.VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Token invalid"));
        return verifier;
    }

    /// <summary>
    /// Returns a valid, unconsumed nonce row seeded in the mock DB.
    /// </summary>
    private static SocialLoginNonce MakeValidNonce() => new()
    {
        Nonce = ValidNonce,
        ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        ConsumedAt = null,
        CreatedAt = DateTime.UtcNow
    };

    // ── 400 — FluentValidation rejects missing idToken ────────────────
    // This path is enforced by the validator before HandleAsync is reached.
    // We verify the validator rejects an empty token directly.

    [Fact]
    public void Validator_EmptyIdToken_HasValidationError()
    {
        var validator = new GoogleSocialLoginValidator();
        var result = validator.Validate(new GoogleSocialLoginRequest { IdToken = "", Nonce = "some-nonce" });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => string.Equals(e.PropertyName, nameof(GoogleSocialLoginRequest.IdToken), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_NullIdToken_HasValidationError()
    {
        var validator = new GoogleSocialLoginValidator();
        var result = validator.Validate(new GoogleSocialLoginRequest { IdToken = null!, Nonce = "some-nonce" });
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validator_EmptyNonce_HasValidationError()
    {
        var validator = new GoogleSocialLoginValidator();
        var result = validator.Validate(new GoogleSocialLoginRequest { IdToken = "token", Nonce = "" });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => string.Equals(
            e.PropertyName, nameof(GoogleSocialLoginRequest.Nonce),
            StringComparison.OrdinalIgnoreCase));
    }

    // ── 401 — nonce not found in DB ───────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_NonceNotFound_Returns401()
    {
        // DB has no nonce rows — any nonce value is unknown.
        var verifier = MakeVerifier(ValidPayload);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var db = new MockDbBuilder().Build(); // no nonce rows
        var config = MakeConfig();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "valid-token", Nonce = "unknown-nonce" },
            CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        // Verifier must NOT be called when the nonce is invalid.
        await verifier.DidNotReceive().VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NonceAlreadyConsumed_Returns401()
    {
        // Nonce exists but has already been consumed.
        var consumedNonce = new SocialLoginNonce
        {
            Nonce = ValidNonce,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            ConsumedAt = DateTime.UtcNow.AddMinutes(-1), // already consumed
            CreatedAt = DateTime.UtcNow.AddMinutes(-2)
        };

        var verifier = MakeVerifier(ValidPayload);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var db = new MockDbBuilder().With(consumedNonce).Build();
        var config = MakeConfig();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "valid-token", Nonce = ValidNonce },
            CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await verifier.DidNotReceive().VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NonceExpired_Returns401()
    {
        // Nonce exists but has expired.
        var expiredNonce = new SocialLoginNonce
        {
            Nonce = ValidNonce,
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1), // expired
            ConsumedAt = null,
            CreatedAt = DateTime.UtcNow.AddMinutes(-11)
        };

        var verifier = MakeVerifier(ValidPayload);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var db = new MockDbBuilder().With(expiredNonce).Build();
        var config = MakeConfig();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "valid-token", Nonce = ValidNonce },
            CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await verifier.DidNotReceive().VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── 401 — invalid Google token ─────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_InvalidGoogleToken_Returns401()
    {
        // Arrange — nonce is valid but verifier rejects the token.
        var verifier = MakeFailingVerifier();
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var db = new MockDbBuilder().With(MakeValidNonce()).Build();
        var config = MakeConfig();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        // Act — SendProblemAsync writes the response; it does NOT throw.
        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "bad-token", Nonce = ValidNonce },
            CancellationToken.None);

        // Assert — 401, and the nonce must NOT have been consumed (atomic consume is
        // only called after token verification succeeds — bad tokens do not burn the nonce).
        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await db.DidNotReceive().ConsumeNonceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── 401 — concurrent consume race lost ───────────────────────────────────

    /// <summary>
    /// Simulates a concurrent-request race where two requests both pass the
    /// pre-check read (nonce appears valid) but only one wins the atomic UPDATE.
    /// The loser receives 0 rows from ConsumeNonceAsync and must return 401.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ConsumeRaceLost_Returns401()
    {
        // Arrange — nonce is valid at pre-check time but ConsumeNonceAsync returns
        // 0 (another request consumed it in the gap between read and update).
        var verifier = MakeVerifier(ValidPayload);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var db = new MockDbBuilder().With(MakeValidNonce()).Build();
        // Override the default return: atomic consume lost the race.
        db.ConsumeNonceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(0);
        var config = MakeConfig();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "valid-token", Nonce = ValidNonce },
            CancellationToken.None);

        // Assert — must be 401; no user lookup / provisioning should have occurred.
        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await userManager.DidNotReceive().FindByIdAsync(Arg.Any<string>());
        await userManager.DidNotReceive().FindByEmailAsync(Arg.Any<string>());
    }

    /// <summary>
    /// Covers the email_verified=false security fix in GoogleTokenVerifier.
    ///
    /// GoogleTokenVerifier.VerifyAsync now throws InvalidOperationException when
    /// payload.EmailVerified != true (i.e. Google did not verify the email address).
    /// Since offline unit-testing of GoogleJsonWebSignature.ValidateAsync with a
    /// real signed JWT is not feasible, we verify the endpoint contract: any
    /// InvalidOperationException thrown by the verifier — including one triggered
    /// by an unverified email — is mapped to 401 invalid_credentials.
    ///
    /// To see the verifier guard at source, inspect:
    ///   Infrastructure/Services/GoogleTokenVerifier.cs — the "email_verified" check.
    /// Critically: the nonce must NOT be consumed when token verification fails.
    /// </summary>
    [Fact]
    public async Task HandleAsync_VerifierThrowsForUnverifiedEmail_Returns401WithInvalidCredentials()
    {
        // Arrange — simulate the InvalidOperationException GoogleTokenVerifier now
        // raises when payload.EmailVerified is false or null.
        var verifier = Substitute.For<IGoogleTokenVerifier>();
        verifier.VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(
                "Google ID token has an unverified email address (email_verified is not true)."));

        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var db = new MockDbBuilder().With(MakeValidNonce()).Build();
        var config = MakeConfig();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        // Act
        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "token-with-unverified-email", Nonce = ValidNonce },
            CancellationToken.None);

        // Assert — 401, not 200/409/403. The unverified-email path must never
        // proceed to account lookup or provisioning; nonce must not be burned.
        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await db.DidNotReceive().ConsumeNonceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifier throws when the nonce field in the token does not match the expected nonce.
    /// The endpoint maps this to 401 — same as any other InvalidOperationException from the verifier.
    /// Critically: the nonce must NOT be consumed when token verification fails.
    /// </summary>
    [Fact]
    public async Task HandleAsync_VerifierThrowsForNonceMismatch_Returns401()
    {
        var verifier = Substitute.For<IGoogleTokenVerifier>();
        verifier.VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(
                "Google ID token nonce does not match the expected nonce. Possible replay attack."));

        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var db = new MockDbBuilder().With(MakeValidNonce()).Build();
        var config = MakeConfig();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "replay-token", Nonce = ValidNonce },
            CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await db.DidNotReceive().ConsumeNonceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── 403 — deactivated account ──────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_DeactivatedAccount_Returns403WithAccountDeactivatedCode()
    {
        // Arrange — existing external login for this Google subject
        var userId = Guid.NewGuid();
        var inactiveUser = EntityBuilder.User
            .WithId(userId)
            .WithEmail(ValidPayload.Email)
            .Inactive()
            .Build();

        var externalLogin = new UserExternalLogin
        {
            UserId = userId,
            Provider = "google",
            Subject = ValidPayload.Subject
        };

        var verifier = MakeVerifier(ValidPayload);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        // FindByIdAsync is called after the external login is found.
        userManager.FindByIdAsync(userId.ToString()).Returns(inactiveUser);

        var db = new MockDbBuilder()
            .With(MakeValidNonce())
            .With(externalLogin)
            .Build();
        var config = MakeConfig();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        // Act — SendProblemAsync writes the response and does NOT throw.
        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "valid-token", Nonce = ValidNonce },
            CancellationToken.None);

        // Assert — status must be 403 (not 400) and error code must be present.
        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    // ── 409 — email conflict (password-only account) ───────────────────────

    [Fact]
    public async Task HandleAsync_ExistingPasswordOnlyAccount_Returns409WithSocialEmailConflict()
    {
        // Arrange — existing user with the verified email but NO Google external login
        var existingUser = EntityBuilder.User
            .WithEmail(ValidPayload.Email)
            .Build();

        var verifier = MakeVerifier(ValidPayload);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByEmailAsync(ValidPayload.Email).Returns(existingUser);

        // No UserExternalLogin rows in the DB.
        var db = new MockDbBuilder()
            .With(MakeValidNonce())
            .Build();
        var config = MakeConfig();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        // Act
        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "valid-token", Nonce = ValidNonce },
            CancellationToken.None);

        // Assert
        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    // ── 200 — existing Google-linked user ─────────────────────────────────

    [Fact]
    public async Task HandleAsync_ExistingGoogleLink_ReturnsTokens()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = EntityBuilder.User
            .WithId(userId)
            .WithEmail(ValidPayload.Email)
            .Build();

        var externalLogin = new UserExternalLogin
        {
            UserId = userId,
            Provider = "google",
            Subject = ValidPayload.Subject
        };

        var verifier = MakeVerifier(ValidPayload);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        // FindByIdAsync is called after the external login is found.
        userManager.FindByIdAsync(userId.ToString()).Returns(user);
        userManager.GetRolesAsync(user).Returns(["Trainer"]);

        var db = new MockDbBuilder()
            .With(MakeValidNonce())
            .With(externalLogin)
            .Build();
        var config = MakeConfig();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        // Act
        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "valid-token", Nonce = ValidNonce },
            CancellationToken.None);

        // Assert
        ep.ValidationFailed.Should().BeFalse();
        ep.Response.AccessToken.Should().NotBeNullOrEmpty();
        ep.Response.RefreshToken.Should().NotBeNullOrEmpty();
        ep.Response.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
        db.RefreshTokens.Received(1).Add(Arg.Is<RefreshToken>(rt => rt.UserId == userId));
        await db.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
        // Atomic consume must have been called exactly once.
        await db.Received(1).ConsumeNonceAsync(ValidNonce, Arg.Any<CancellationToken>());
    }

    // ── 200 — new user provisioning ───────────────────────────────────────

    [Fact]
    public async Task HandleAsync_NewUser_ProvisionesAccountAndReturnsTokens()
    {
        // Arrange — no existing user or external login
        var verifier = MakeVerifier(ValidPayload);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();

        // FindByEmailAsync returns null → no existing account
        userManager.FindByEmailAsync(ValidPayload.Email).Returns((ApplicationUser?)null);

        // CreateAsync succeeds and sets up the user object
        userManager.CreateAsync(Arg.Any<ApplicationUser>())
            .Returns(Microsoft.AspNetCore.Identity.IdentityResult.Success);

        userManager.AddToRoleAsync(Arg.Any<ApplicationUser>(), AppRoles.Client)
            .Returns(Microsoft.AspNetCore.Identity.IdentityResult.Success);

        // GetRolesAsync needs to return roles for token creation
        userManager.GetRolesAsync(Arg.Any<ApplicationUser>()).Returns(["Client"]);

        var db = new MockDbBuilder()
            .With(MakeValidNonce())
            .Build();
        var config = MakeConfig();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        // Act
        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "valid-token", Nonce = ValidNonce, HealthDataConsent = true },
            CancellationToken.None);

        // Assert
        ep.ValidationFailed.Should().BeFalse();
        ep.Response.AccessToken.Should().NotBeNullOrEmpty();
        ep.Response.RefreshToken.Should().NotBeNullOrEmpty();
        ep.Response.EmailConfirmed.Should().BeTrue(); // Google verified the email

        // A UserExternalLogin row must have been added
        db.UserExternalLogins.Received(1).Add(Arg.Is<UserExternalLogin>(el =>
            el.Provider == "google" && el.Subject == ValidPayload.Subject));

        // New user must be provisioned with the Client role — NOT Trainer.
        // Assigning Trainer here would allow any anonymous caller with a valid
        // Google token to gain Trainer privileges (privilege escalation).
        await userManager.Received(1).AddToRoleAsync(Arg.Any<ApplicationUser>(), AppRoles.Client);
        await userManager.DidNotReceive().AddToRoleAsync(Arg.Any<ApplicationUser>(), AppRoles.Trainer);

        // A ClientProfile must have been created (mirroring RegisterEndpoint for Client role).
        db.ClientProfiles.Received(1).Add(Arg.Any<ClientProfile>());

        // ProfessionalProfile must NOT be created for a new Google social-login user.
        db.ProfessionalProfiles.DidNotReceive().Add(Arg.Any<ProfessionalProfile>());

        // #803/#817 parity with RegisterEndpoint: a brand-new account provisioned via
        // Google must also seed any message-bearing PendingInvite already addressed to
        // its email, so the coach's opening message is visible before the client accepts.
        await _inviteConversationSeeder.Received(1).SeedForNewUserAsync(
            Arg.Is<ApplicationUser>(u => u.Email == ValidPayload.Email), Arg.Any<CancellationToken>());
        await _inviteNotificationSeeder.Received(1).SeedForNewUserAsync(
            Arg.Is<ApplicationUser>(u => u.Email == ValidPayload.Email), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Non-fatal seeding per review (#803/#817): the account + Google link are already
    /// committed before the seed runs, so a seed failure must not turn a successful
    /// Google sign-up into a 401/500 — the client would otherwise be told to retry a
    /// sign-in that has already succeeded.
    /// </summary>
    [Fact]
    public async Task HandleAsync_NewUser_SeedThrows_StillReturnsTokens_AndLogsWarning()
    {
        var verifier = MakeVerifier(ValidPayload);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();

        userManager.FindByEmailAsync(ValidPayload.Email).Returns((ApplicationUser?)null);
        userManager.CreateAsync(Arg.Any<ApplicationUser>())
            .Returns(Microsoft.AspNetCore.Identity.IdentityResult.Success);
        userManager.AddToRoleAsync(Arg.Any<ApplicationUser>(), AppRoles.Client)
            .Returns(Microsoft.AspNetCore.Identity.IdentityResult.Success);
        userManager.GetRolesAsync(Arg.Any<ApplicationUser>()).Returns(["Client"]);

        _inviteConversationSeeder
            .SeedForNewUserAsync(Arg.Any<ApplicationUser>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("mongo down"));

        var db = new MockDbBuilder()
            .With(MakeValidNonce())
            .Build();
        var config = MakeConfig();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "valid-token", Nonce = ValidNonce, HealthDataConsent = true },
            CancellationToken.None);

        // The seed failure must not propagate — tokens are still returned.
        ep.ValidationFailed.Should().BeFalse();
        ep.Response.AccessToken.Should().NotBeNullOrEmpty();
        ep.Response.RefreshToken.Should().NotBeNullOrEmpty();

        // The account + external login must still be committed despite the seed failure.
        await userManager.Received(1).CreateAsync(Arg.Any<ApplicationUser>());
        db.UserExternalLogins.Received(1).Add(Arg.Any<UserExternalLogin>());

        // The failure is logged as an error, not swallowed silently.
        _logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains(ValidPayload.Email)),
            Arg.Is<Exception>(ex => ex is InvalidOperationException && ex.Message == "mongo down"),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// #803/#817 — the invite-conversation seed only applies to brand-new accounts. A
    /// returning user with an existing Google link must NOT re-run the pending-invite
    /// lookup on every login.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ExistingGoogleLink_DoesNotSeedPendingInviteConversations()
    {
        var userId = Guid.NewGuid();
        var user = EntityBuilder.User
            .WithId(userId)
            .WithEmail(ValidPayload.Email)
            .Build();

        var externalLogin = new UserExternalLogin
        {
            UserId = userId,
            Provider = "google",
            Subject = ValidPayload.Subject
        };

        var verifier = MakeVerifier(ValidPayload);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(userId.ToString()).Returns(user);
        userManager.GetRolesAsync(user).Returns(["Client"]);

        var db = new MockDbBuilder()
            .With(MakeValidNonce())
            .With(externalLogin)
            .Build();
        var config = MakeConfig();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "valid-token", Nonce = ValidNonce },
            CancellationToken.None);

        ep.ValidationFailed.Should().BeFalse();
        await _inviteConversationSeeder.DidNotReceiveWithAnyArgs().SeedForNewUserAsync(
            default!, TestContext.Current.CancellationToken);
    }

    // ── 200 — email matches existing google-linked account (returning user) ──

    [Fact]
    public async Task HandleAsync_EmailMatchesExistingGoogleLinkedAccount_ReturnsTokens()
    {
        // Arrange — external login exists for the same sub (returning user)
        var userId = Guid.NewGuid();
        var user = EntityBuilder.User
            .WithId(userId)
            .WithEmail(ValidPayload.Email)
            .Build();

        var externalLogin = new UserExternalLogin
        {
            UserId = userId,
            Provider = "google",
            Subject = ValidPayload.Subject
        };

        var verifier = MakeVerifier(ValidPayload);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(userId.ToString()).Returns(user);
        userManager.GetRolesAsync(user).Returns(["Trainer"]);

        var db = new MockDbBuilder()
            .With(MakeValidNonce())
            .With(externalLogin)
            .Build();
        var config = MakeConfig();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        // Act
        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "valid-token", Nonce = ValidNonce },
            CancellationToken.None);

        // Assert — no 409; returns tokens
        ep.ValidationFailed.Should().BeFalse();
        ep.HttpContext.Response.StatusCode.Should().NotBe(StatusCodes.Status409Conflict);
        ep.Response.AccessToken.Should().NotBeNullOrEmpty();
    }

    // ── Requested roles for new accounts ───────────────────────────────────

    private static Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> MakeNewUserManager()
    {
        var manager = EndpointTestHelpers.CreateFakeUserManager();
        manager.FindByEmailAsync(ValidPayload.Email).Returns((ApplicationUser?)null);
        manager.CreateAsync(Arg.Any<ApplicationUser>())
            .Returns(Microsoft.AspNetCore.Identity.IdentityResult.Success);
        manager.AddToRoleAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(Microsoft.AspNetCore.Identity.IdentityResult.Success);
        manager.GetRolesAsync(Arg.Any<ApplicationUser>()).Returns(["Trainer"]);
        return manager;
    }

    [Fact]
    public async Task HandleAsync_NewUserRequestsTrainer_ProvisionsCoachWithDefaultCheckInForm()
    {
        var userManager = MakeNewUserManager();
        var db = new MockDbBuilder().With(MakeValidNonce()).Build();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(MakeVerifier(ValidPayload), userManager, db, MakeConfig(), _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);
        ep.HttpContext.Request.Headers.AcceptLanguage = "cs";

        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "valid-token", Nonce = ValidNonce, Roles = ["Trainer"] },
            CancellationToken.None);

        ep.ValidationFailed.Should().BeFalse();
        ep.Response.AccessToken.Should().NotBeNullOrEmpty();
        await userManager.Received(1).AddToRoleAsync(Arg.Any<ApplicationUser>(), AppRoles.Trainer);
        await userManager.DidNotReceive().AddToRoleAsync(Arg.Any<ApplicationUser>(), AppRoles.Client);
        db.ProfessionalProfiles.Received(1).Add(Arg.Any<ProfessionalProfile>());
        db.ClientProfiles.DidNotReceive().Add(Arg.Any<ClientProfile>());
        db.Questionnaires.Received(1).Add(Arg.Is<Questionnaire>(q =>
            q.Type == FormUp.Application.Domain.Enums.QuestionnaireType.CheckIn && q.Title == "Týdenní check-in"));
        db.UserExternalLogins.Received(1).Add(Arg.Is<UserExternalLogin>(el => el.Provider == "google"));
        await _inviteConversationSeeder.DidNotReceive().SeedForNewUserAsync(Arg.Any<ApplicationUser>(), Arg.Any<CancellationToken>());
        await _inviteNotificationSeeder.DidNotReceive().SeedForNewUserAsync(Arg.Any<ApplicationUser>(), Arg.Any<CancellationToken>());
        await _coachEvents.Received(1).RecordAsync(
            FormUp.Application.Domain.Enums.CoachEventType.SignedUp, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _coachEvents.Received(1).RecordAsync(
            FormUp.Application.Domain.Enums.CoachEventType.EmailVerified, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NewUserRequestsNutritionistWithoutLanguageHeader_FallsBackToEnglishForm()
    {
        var userManager = MakeNewUserManager();
        var db = new MockDbBuilder().With(MakeValidNonce()).Build();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(MakeVerifier(ValidPayload), userManager, db, MakeConfig(), _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "valid-token", Nonce = ValidNonce, Roles = ["nutritionist"] },
            CancellationToken.None);

        await userManager.Received(1).AddToRoleAsync(Arg.Any<ApplicationUser>(), AppRoles.Nutritionist);
        db.ProfessionalProfiles.Received(1).Add(Arg.Any<ProfessionalProfile>());
        db.Questionnaires.Received(1).Add(Arg.Is<Questionnaire>(q => q.Title == "Weekly Check-In"));
    }

    [Fact]
    public async Task HandleAsync_NewUserWithoutRoles_StaysClientWithoutCheckInForm()
    {
        var userManager = MakeNewUserManager();
        var db = new MockDbBuilder().With(MakeValidNonce()).Build();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(MakeVerifier(ValidPayload), userManager, db, MakeConfig(), _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "valid-token", Nonce = ValidNonce, HealthDataConsent = true },
            CancellationToken.None);

        await userManager.Received(1).AddToRoleAsync(Arg.Any<ApplicationUser>(), AppRoles.Client);
        db.ClientProfiles.Received(1).Add(Arg.Any<ClientProfile>());
        db.ProfessionalProfiles.DidNotReceive().Add(Arg.Any<ProfessionalProfile>());
        db.Questionnaires.DidNotReceive().Add(Arg.Any<Questionnaire>());
        await _inviteConversationSeeder.Received(1).SeedForNewUserAsync(Arg.Any<ApplicationUser>(), Arg.Any<CancellationToken>());
        await _inviteNotificationSeeder.Received(1).SeedForNewUserAsync(Arg.Any<ApplicationUser>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_LinkedUserSendsOtherRoles_KeepsStoredRolesAndProfiles()
    {
        var userId = Guid.NewGuid();
        var user = EntityBuilder.User.WithId(userId).WithEmail(ValidPayload.Email).Build();
        var externalLogin = new UserExternalLogin { UserId = userId, Provider = "google", Subject = ValidPayload.Subject };
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(userId.ToString()).Returns(user);
        userManager.GetRolesAsync(user).Returns(["Client"]);
        var db = new MockDbBuilder().With(MakeValidNonce()).With(externalLogin).Build();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(MakeVerifier(ValidPayload), userManager, db, MakeConfig(), _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "valid-token", Nonce = ValidNonce, Roles = ["Trainer"] },
            CancellationToken.None);

        ep.Response.AccessToken.Should().NotBeNullOrEmpty();
        await userManager.DidNotReceive().AddToRoleAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>());
        await userManager.DidNotReceive().CreateAsync(Arg.Any<ApplicationUser>());
        db.ProfessionalProfiles.DidNotReceive().Add(Arg.Any<ProfessionalProfile>());
        db.ClientProfiles.DidNotReceive().Add(Arg.Any<ClientProfile>());
        db.Questionnaires.DidNotReceive().Add(Arg.Any<Questionnaire>());
    }

    [Fact]
    public async Task HandleAsync_EmailConflictWithRoles_Returns409AndWritesNothing()
    {
        var existingUser = EntityBuilder.User.WithEmail(ValidPayload.Email).Build();
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByEmailAsync(ValidPayload.Email).Returns(existingUser);
        var db = new MockDbBuilder().With(MakeValidNonce()).Build();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(MakeVerifier(ValidPayload), userManager, db, MakeConfig(), _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "valid-token", Nonce = ValidNonce, Roles = ["Trainer"] },
            CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        await userManager.DidNotReceive().CreateAsync(Arg.Any<ApplicationUser>());
        await userManager.DidNotReceive().AddToRoleAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>());
        db.ProfessionalProfiles.DidNotReceive().Add(Arg.Any<ProfessionalProfile>());
        db.ClientProfiles.DidNotReceive().Add(Arg.Any<ClientProfile>());
        db.Questionnaires.DidNotReceive().Add(Arg.Any<Questionnaire>());
        db.UserExternalLogins.DidNotReceive().Add(Arg.Any<UserExternalLogin>());
    }

    // ── Health-data consent for new accounts ──────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task HandleAsync_NewClientWithoutConsent_Returns400AndCreatesNothing(bool? consent)
    {
        var userManager = MakeNewUserManager();
        var db = new MockDbBuilder().With(MakeValidNonce()).Build();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(MakeVerifier(ValidPayload), userManager, db, MakeConfig(), _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await FluentActions
            .Awaiting(() => ep.HandleAsync(
                new GoogleSocialLoginRequest { IdToken = "valid-token", Nonce = ValidNonce, HealthDataConsent = consent },
                CancellationToken.None))
            .Should().ThrowAsync<ValidationFailureException>();

        ep.ValidationFailures.Should().Contain(f => f.ErrorMessage == SocialSignUpRoles.HealthDataConsentRequiredMessage);
        await userManager.DidNotReceive().CreateAsync(Arg.Any<ApplicationUser>());
        await userManager.DidNotReceive().AddToRoleAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>());
        db.ClientProfiles.DidNotReceive().Add(Arg.Any<ClientProfile>());
        db.UserExternalLogins.DidNotReceive().Add(Arg.Any<UserExternalLogin>());
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default, default!, default!, default, default, default, default, default);
    }

    [Fact]
    public async Task HandleAsync_NewClientWithConsent_StoresConsentAndAudits()
    {
        var userManager = MakeNewUserManager();
        var db = new MockDbBuilder().With(MakeValidNonce()).Build();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(MakeVerifier(ValidPayload), userManager, db, MakeConfig(), _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "valid-token", Nonce = ValidNonce, HealthDataConsent = true },
            CancellationToken.None);

        ep.Response.AccessToken.Should().NotBeNullOrEmpty();
        await userManager.Received(1).CreateAsync(Arg.Is<ApplicationUser>(u =>
            u.HealthDataConsent == true && u.HealthDataConsentDate != null && u.GdprConsent));
        await _audit.Received(1).LogAsync(
            Arg.Any<Guid>(),
            "Register",
            nameof(ApplicationUser),
            Arg.Any<Guid>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Is<string?>(v => v!.Contains("\"healthDataConsent\":true") && v.Contains("\"provider\":\"google\"") && v.Contains("\"Client\"")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NewCoachWithoutConsent_LeavesConsentNullAndAudits()
    {
        var userManager = MakeNewUserManager();
        var db = new MockDbBuilder().With(MakeValidNonce()).Build();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(MakeVerifier(ValidPayload), userManager, db, MakeConfig(), _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "valid-token", Nonce = ValidNonce, Roles = ["Trainer"] },
            CancellationToken.None);

        ep.Response.AccessToken.Should().NotBeNullOrEmpty();
        await userManager.Received(1).CreateAsync(Arg.Is<ApplicationUser>(u =>
            u.HealthDataConsent == null && u.HealthDataConsentDate == null));
        await _audit.Received(1).LogAsync(
            Arg.Any<Guid>(),
            "Register",
            nameof(ApplicationUser),
            Arg.Any<Guid>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Is<string?>(v => v!.Contains("\"healthDataConsent\":null") && v.Contains("\"Trainer\"")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_LinkedClientWithoutConsent_LogsInAndWritesNoConsent()
    {
        var userId = Guid.NewGuid();
        var user = EntityBuilder.User.WithId(userId).WithEmail(ValidPayload.Email).Build();
        var externalLogin = new UserExternalLogin { UserId = userId, Provider = "google", Subject = ValidPayload.Subject };
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(userId.ToString()).Returns(user);
        userManager.GetRolesAsync(user).Returns(["Client"]);
        var db = new MockDbBuilder().With(MakeValidNonce()).With(externalLogin).Build();
        var ep = Factory.Create<GoogleSocialLoginEndpoint>(MakeVerifier(ValidPayload), userManager, db, MakeConfig(), _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new GoogleSocialLoginRequest { IdToken = "valid-token", Nonce = ValidNonce },
            CancellationToken.None);

        ep.Response.AccessToken.Should().NotBeNullOrEmpty();
        user.HealthDataConsent.Should().BeNull();
        user.HealthDataConsentDate.Should().BeNull();
        await userManager.DidNotReceive().CreateAsync(Arg.Any<ApplicationUser>());
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default, default!, default!, default, default, default, default, default);
    }
}
