using FastEndpoints;
using FastEndpoints.Testing;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Auth.Shared;
using FormUp.Application.Features.Auth.SocialLogin.Apple;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FormUp.Tests.Endpoints.Auth;

/// <summary>
/// Unit tests for <see cref="AppleSocialLoginEndpoint"/>.
/// A fake <see cref="IAppleTokenVerifier"/> is injected so no real Apple
/// network calls are made.
/// </summary>
public class AppleSocialLoginEndpointTests
{
    private static readonly string JwtSecret = new('x', 64);
    private readonly IPendingInviteConversationSeeder _inviteConversationSeeder = Substitute.For<IPendingInviteConversationSeeder>();
    private readonly IPendingInviteNotificationSeeder _inviteNotificationSeeder = Substitute.For<IPendingInviteNotificationSeeder>();
    private readonly ILogger<AppleSocialLoginEndpoint> _logger = Substitute.For<ILogger<AppleSocialLoginEndpoint>>();
    private readonly FormUp.Application.Domain.Interfaces.ICoachEventRecorder _coachEvents = Substitute.For<FormUp.Application.Domain.Interfaces.ICoachEventRecorder>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();

    // Raw nonce used in all tests — the DB is seeded with a valid record for this value.
    private const string ValidNonce = "test-raw-nonce-apple";

    // Shared Apple token payload returned by the fake verifier — verified email, not private-relay.
    private static readonly AppleTokenPayload ValidPayloadWithEmail = new(
        Subject: "apple-sub-12345",
        Email: "appleuser@example.com",
        EmailVerified: true,
        IsPrivateEmail: false);

    // Payload simulating a private-relay email (first auth with hidden real email).
    private static readonly AppleTokenPayload PrivateRelayPayload = new(
        Subject: "apple-sub-67890",
        Email: "abc123@privaterelay.appleid.com",
        EmailVerified: true,
        IsPrivateEmail: true);

    // Payload simulating a returning user (Apple omits email after first auth).
    private static readonly AppleTokenPayload ReturningUserPayload = new(
        Subject: "apple-sub-12345",
        Email: null,
        EmailVerified: false,
        IsPrivateEmail: false);

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
    private static IAppleTokenVerifier MakeVerifier(AppleTokenPayload payload)
    {
        var verifier = Substitute.For<IAppleTokenVerifier>();
        verifier.VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(payload);
        return verifier;
    }

    private static IAppleTokenVerifier MakeFailingVerifier()
    {
        var verifier = Substitute.For<IAppleTokenVerifier>();
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

    // ── 400 — FluentValidation rejects missing identityToken ─────────────────

    [Fact]
    public void Validator_EmptyIdentityToken_HasValidationError()
    {
        var validator = new AppleSocialLoginValidator();
        var result = validator.Validate(new AppleSocialLoginRequest { IdentityToken = "", Nonce = "some-nonce" });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => string.Equals(
            e.PropertyName, nameof(AppleSocialLoginRequest.IdentityToken),
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_NullIdentityToken_HasValidationError()
    {
        var validator = new AppleSocialLoginValidator();
        var result = validator.Validate(new AppleSocialLoginRequest { IdentityToken = null!, Nonce = "some-nonce" });
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validator_EmptyNonce_HasValidationError()
    {
        var validator = new AppleSocialLoginValidator();
        var result = validator.Validate(new AppleSocialLoginRequest { IdentityToken = "token", Nonce = "" });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => string.Equals(
            e.PropertyName, nameof(AppleSocialLoginRequest.Nonce),
            StringComparison.OrdinalIgnoreCase));
    }

    // ── 401 — nonce not found in DB ───────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_NonceNotFound_Returns401()
    {
        // DB has no nonce rows — any nonce value is unknown.
        var verifier = MakeVerifier(ValidPayloadWithEmail);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var db = new MockDbBuilder().Build(); // no nonce rows
        var config = MakeConfig();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "valid-token", Nonce = "unknown-nonce" },
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

        var verifier = MakeVerifier(ValidPayloadWithEmail);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var db = new MockDbBuilder().With(consumedNonce).Build();
        var config = MakeConfig();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "valid-token", Nonce = ValidNonce },
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

        var verifier = MakeVerifier(ValidPayloadWithEmail);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var db = new MockDbBuilder().With(expiredNonce).Build();
        var config = MakeConfig();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "valid-token", Nonce = ValidNonce },
            CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await verifier.DidNotReceive().VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── 401 — invalid Apple token ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_InvalidAppleToken_Returns401()
    {
        // Arrange — nonce is valid but verifier rejects the token.
        var verifier = MakeFailingVerifier();
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var db = new MockDbBuilder().With(MakeValidNonce()).Build();
        var config = MakeConfig();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        // Act — SendProblemAsync writes the response; it does NOT throw.
        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "bad-token", Nonce = ValidNonce },
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
        var verifier = MakeVerifier(ValidPayloadWithEmail);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var db = new MockDbBuilder().With(MakeValidNonce()).Build();
        // Override the default return: atomic consume lost the race.
        db.ConsumeNonceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(0);
        var config = MakeConfig();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "valid-token", Nonce = ValidNonce },
            CancellationToken.None);

        // Assert — must be 401; no user lookup / provisioning should have occurred.
        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await userManager.DidNotReceive().FindByIdAsync(Arg.Any<string>());
        await userManager.DidNotReceive().FindByEmailAsync(Arg.Any<string>());
    }

    /// <summary>
    /// Verifier throws when token uses alg=none or HS256 — endpoint must return 401.
    /// The ValidAlgorithms=["RS256"] guard inside AppleTokenVerifier rejects these;
    /// the endpoint maps any InvalidOperationException to 401 invalid_credentials.
    /// Critically: the nonce must NOT be consumed when token verification fails.
    /// </summary>
    [Fact]
    public async Task HandleAsync_VerifierThrowsForAlgConfusion_Returns401()
    {
        // Arrange — simulate the exception raised by alg-confusion guard
        var verifier = Substitute.For<IAppleTokenVerifier>();
        verifier.VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(
                "Apple identity token validation failed after JWKS refresh."));

        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var db = new MockDbBuilder().With(MakeValidNonce()).Build();
        var config = MakeConfig();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        // Act
        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "alg-none-token", Nonce = ValidNonce },
            CancellationToken.None);

        // Assert — must be 401, not 200/409/403; nonce must not be burned on failed token verification.
        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await db.DidNotReceive().ConsumeNonceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifier throws when email is explicitly unverified and not a private-relay address.
    /// The endpoint maps this InvalidOperationException to 401 invalid_credentials.
    /// Critically: the nonce must NOT be consumed when token verification fails.
    /// </summary>
    [Fact]
    public async Task HandleAsync_VerifierThrowsForUnverifiedNonRelayEmail_Returns401()
    {
        var verifier = Substitute.For<IAppleTokenVerifier>();
        verifier.VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(
                "Apple identity token email is explicitly unverified and is not a private-relay address."));

        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var db = new MockDbBuilder().With(MakeValidNonce()).Build();
        var config = MakeConfig();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "unverified-email-token", Nonce = ValidNonce },
            CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await db.DidNotReceive().ConsumeNonceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifier throws when the nonce claim in the token does not match the expected nonce.
    /// The endpoint maps this to 401 — same as any other InvalidOperationException from the verifier.
    /// Critically: the nonce must NOT be consumed when token verification fails.
    /// </summary>
    [Fact]
    public async Task HandleAsync_VerifierThrowsForNonceMismatch_Returns401()
    {
        var verifier = Substitute.For<IAppleTokenVerifier>();
        verifier.VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(
                "Apple identity token nonce claim does not match the expected nonce. Possible replay attack."));

        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var db = new MockDbBuilder().With(MakeValidNonce()).Build();
        var config = MakeConfig();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "replay-token", Nonce = ValidNonce },
            CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await db.DidNotReceive().ConsumeNonceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── 401 — orphaned external login ─────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_OrphanedExternalLogin_Returns401()
    {
        // Arrange — external login exists but linked user does not
        var userId = Guid.NewGuid();
        var externalLogin = new UserExternalLogin
        {
            UserId = userId,
            Provider = "apple",
            Subject = ValidPayloadWithEmail.Subject
        };

        var verifier = MakeVerifier(ValidPayloadWithEmail);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        // FindByIdAsync returns null — orphaned link
        userManager.FindByIdAsync(userId.ToString()).Returns((ApplicationUser?)null);

        var db = new MockDbBuilder()
            .With(MakeValidNonce())
            .With(externalLogin)
            .Build();
        var config = MakeConfig();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "valid-token", Nonce = ValidNonce },
            CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    // ── 422 — no link AND no email in token ───────────────────────────────────

    [Fact]
    public async Task HandleAsync_NoLinkAndNoEmailInToken_Returns422()
    {
        // Arrange — no external login, token has no email (Apple re-auth without link)
        var verifier = MakeVerifier(ReturningUserPayload);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var db = new MockDbBuilder()
            .With(MakeValidNonce())
            .Build(); // no external logins
        var config = MakeConfig();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "no-email-token", Nonce = ValidNonce },
            CancellationToken.None);

        // Assert — 422, not NRE or 401
        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
    }

    // ── 409 — email conflict (password-only account) ──────────────────────────

    [Fact]
    public async Task HandleAsync_ExistingPasswordOnlyAccount_Returns409WithSocialEmailConflict()
    {
        // Arrange — existing user with the verified email but NO Apple external login
        var existingUser = EntityBuilder.User
            .WithEmail(ValidPayloadWithEmail.Email!)
            .Build();

        var verifier = MakeVerifier(ValidPayloadWithEmail);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByEmailAsync(ValidPayloadWithEmail.Email!).Returns(existingUser);

        // No UserExternalLogin rows in the DB.
        var db = new MockDbBuilder()
            .With(MakeValidNonce())
            .Build();
        var config = MakeConfig();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "valid-token", Nonce = ValidNonce },
            CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    // ── 403 — deactivated account ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_DeactivatedAccount_Returns403WithAccountDeactivatedCode()
    {
        // Arrange — existing external login for this Apple subject; account inactive
        var userId = Guid.NewGuid();
        var inactiveUser = EntityBuilder.User
            .WithId(userId)
            .WithEmail(ValidPayloadWithEmail.Email!)
            .Inactive()
            .Build();

        var externalLogin = new UserExternalLogin
        {
            UserId = userId,
            Provider = "apple",
            Subject = ValidPayloadWithEmail.Subject
        };

        var verifier = MakeVerifier(ValidPayloadWithEmail);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(userId.ToString()).Returns(inactiveUser);

        var db = new MockDbBuilder()
            .With(MakeValidNonce())
            .With(externalLogin)
            .Build();
        var config = MakeConfig();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "valid-token", Nonce = ValidNonce },
            CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    // ── 200 — happy path A: existing Apple-linked user (returning) ────────────

    [Fact]
    public async Task HandleAsync_ExistingAppleLink_ReturnsTokens()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = EntityBuilder.User
            .WithId(userId)
            .WithEmail(ValidPayloadWithEmail.Email!)
            .Build();

        var externalLogin = new UserExternalLogin
        {
            UserId = userId,
            Provider = "apple",
            Subject = ValidPayloadWithEmail.Subject
        };

        var verifier = MakeVerifier(ValidPayloadWithEmail);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(userId.ToString()).Returns(user);
        userManager.GetRolesAsync(user).Returns(["Trainer"]);

        var db = new MockDbBuilder()
            .With(MakeValidNonce())
            .With(externalLogin)
            .Build();
        var config = MakeConfig();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "valid-token", Nonce = ValidNonce },
            CancellationToken.None);

        ep.ValidationFailed.Should().BeFalse();
        ep.Response.AccessToken.Should().NotBeNullOrEmpty();
        ep.Response.RefreshToken.Should().NotBeNullOrEmpty();
        ep.Response.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
        db.RefreshTokens.Received(1).Add(Arg.Is<RefreshToken>(rt => rt.UserId == userId));
        await db.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
        // Atomic consume must have been called exactly once.
        await db.Received(1).ConsumeNonceAsync(ValidNonce, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Happy path A variant: re-auth where token carries no email.
    /// The existing (apple, sub) link must be found, and the user's stored name
    /// must NOT be overwritten by the absent body values.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ExistingAppleLinkNoEmailInToken_ReturnsTokens_AndIgnoresAbsentBodyName()
    {
        // Arrange — returning user; token has no email; no name in body
        var userId = Guid.NewGuid();
        var user = EntityBuilder.User
            .WithId(userId)
            .WithEmail("stored@example.com")
            .Build();

        var externalLogin = new UserExternalLogin
        {
            UserId = userId,
            Provider = "apple",
            Subject = ReturningUserPayload.Subject
        };

        var verifier = MakeVerifier(ReturningUserPayload);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(userId.ToString()).Returns(user);
        userManager.GetRolesAsync(user).Returns(["Trainer"]);

        var db = new MockDbBuilder()
            .With(MakeValidNonce())
            .With(externalLogin)
            .Build();
        var config = MakeConfig();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        // Act — no FirstName/LastName in body
        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "valid-token", Nonce = ValidNonce },
            CancellationToken.None);

        // Assert — tokens returned; no name update attempted on userManager
        ep.ValidationFailed.Should().BeFalse();
        ep.Response.AccessToken.Should().NotBeNullOrEmpty();
        // We must NOT have called UpdateAsync on the user (no name overwrite)
        await userManager.DidNotReceive().UpdateAsync(Arg.Any<ApplicationUser>());
    }

    // ── 200 — happy path B: new user provisioning with private-relay email ────

    [Fact]
    public async Task HandleAsync_NewUserWithPrivateRelayEmail_ProvisionesAccountAndReturnsTokens()
    {
        // Arrange — no existing user or external login; Apple private-relay email
        var verifier = MakeVerifier(PrivateRelayPayload);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();

        // FindByEmailAsync returns null → no existing account
        userManager.FindByEmailAsync(PrivateRelayPayload.Email!).Returns((ApplicationUser?)null);

        // CreateAsync succeeds and sets up the user object
        userManager.CreateAsync(Arg.Any<ApplicationUser>())
            .Returns(Microsoft.AspNetCore.Identity.IdentityResult.Success);

        userManager.AddToRoleAsync(Arg.Any<ApplicationUser>(), AppRoles.Client)
            .Returns(Microsoft.AspNetCore.Identity.IdentityResult.Success);

        userManager.GetRolesAsync(Arg.Any<ApplicationUser>()).Returns(["Client"]);

        var db = new MockDbBuilder()
            .With(MakeValidNonce())
            .Build();
        var config = MakeConfig();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        // Act — name from body (first auth)
        await ep.HandleAsync(
            new AppleSocialLoginRequest
            {
                IdentityToken = "valid-token",
                FirstName = "Anna",
                LastName = "Smith",
                Nonce = ValidNonce, HealthDataConsent = true
            },
            CancellationToken.None);

        // Assert
        ep.ValidationFailed.Should().BeFalse();
        ep.Response.AccessToken.Should().NotBeNullOrEmpty();
        ep.Response.RefreshToken.Should().NotBeNullOrEmpty();
        ep.Response.EmailConfirmed.Should().BeTrue(); // Apple verified it

        // Provisioned user must have FirstName/LastName from the body
        await userManager.Received(1).CreateAsync(
            Arg.Is<ApplicationUser>(u =>
                u.FirstName == "Anna" &&
                u.LastName == "Smith" &&
                u.EmailConfirmed == true &&
                u.Email == PrivateRelayPayload.Email));

        // A UserExternalLogin row must have been added for apple
        db.UserExternalLogins.Received(1).Add(Arg.Is<UserExternalLogin>(el =>
            el.Provider == "apple" && el.Subject == PrivateRelayPayload.Subject));

        // New user must be provisioned with the Client role — NOT Trainer.
        // Assigning Trainer here would allow any anonymous caller with a valid
        // Apple token to gain Trainer privileges (privilege escalation).
        await userManager.Received(1).AddToRoleAsync(Arg.Any<ApplicationUser>(), AppRoles.Client);
        await userManager.DidNotReceive().AddToRoleAsync(Arg.Any<ApplicationUser>(), AppRoles.Trainer);

        // A ClientProfile must have been created (mirroring RegisterEndpoint for Client role).
        db.ClientProfiles.Received(1).Add(Arg.Any<ClientProfile>());

        // ProfessionalProfile must NOT be created for a new Apple social-login user.
        db.ProfessionalProfiles.DidNotReceive().Add(Arg.Any<ProfessionalProfile>());

        // #803/#817 parity with RegisterEndpoint: a brand-new account provisioned via
        // Apple must also seed any message-bearing PendingInvite already addressed to
        // its email, so the coach's opening message is visible before the client accepts.
        await _inviteConversationSeeder.Received(1).SeedForNewUserAsync(
            Arg.Is<ApplicationUser>(u => u.Email == PrivateRelayPayload.Email), Arg.Any<CancellationToken>());
        await _inviteNotificationSeeder.Received(1).SeedForNewUserAsync(
            Arg.Is<ApplicationUser>(u => u.Email == PrivateRelayPayload.Email), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Non-fatal seeding per review (#803/#817): the account + Apple link are already
    /// committed before the seed runs, so a seed failure must not turn a successful
    /// Apple sign-up into a 401/500 — the client would otherwise be told to retry a
    /// sign-in that has already succeeded.
    /// </summary>
    [Fact]
    public async Task HandleAsync_NewUser_SeedThrows_StillReturnsTokens_AndLogsWarning()
    {
        var verifier = MakeVerifier(PrivateRelayPayload);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();

        userManager.FindByEmailAsync(PrivateRelayPayload.Email!).Returns((ApplicationUser?)null);
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
        var ep = Factory.Create<AppleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new AppleSocialLoginRequest
            {
                IdentityToken = "valid-token",
                FirstName = "Anna",
                LastName = "Smith",
                Nonce = ValidNonce, HealthDataConsent = true
            },
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
            Arg.Is<object>(o => o.ToString()!.Contains(PrivateRelayPayload.Email!)),
            Arg.Is<Exception>(ex => ex is InvalidOperationException && ex.Message == "mongo down"),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// #803/#817 — the invite-conversation seed only applies to brand-new accounts. A
    /// returning user with an existing Apple link must NOT re-run the pending-invite
    /// lookup on every login.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ExistingAppleLink_DoesNotSeedPendingInviteConversations()
    {
        var userId = Guid.NewGuid();
        var user = EntityBuilder.User
            .WithId(userId)
            .WithEmail(ValidPayloadWithEmail.Email!)
            .Build();

        var externalLogin = new UserExternalLogin
        {
            UserId = userId,
            Provider = "apple",
            Subject = ValidPayloadWithEmail.Subject
        };

        var verifier = MakeVerifier(ValidPayloadWithEmail);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(userId.ToString()).Returns(user);
        userManager.GetRolesAsync(user).Returns(["Client"]);

        var db = new MockDbBuilder()
            .With(MakeValidNonce())
            .With(externalLogin)
            .Build();
        var config = MakeConfig();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "valid-token", Nonce = ValidNonce },
            CancellationToken.None);

        ep.ValidationFailed.Should().BeFalse();
        await _inviteConversationSeeder.DidNotReceiveWithAnyArgs().SeedForNewUserAsync(
            default!, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Happy path B variant: new user with no name in body.
    /// FirstName and LastName must fall back to empty string, never null
    /// (ApplicationUser.FirstName/LastName are non-null string fields).
    /// </summary>
    [Fact]
    public async Task HandleAsync_NewUserNoNameInBody_ProvisionesWithEmptyStrings()
    {
        var verifier = MakeVerifier(ValidPayloadWithEmail);
        var userManager = EndpointTestHelpers.CreateFakeUserManager();

        userManager.FindByEmailAsync(ValidPayloadWithEmail.Email!).Returns((ApplicationUser?)null);
        userManager.CreateAsync(Arg.Any<ApplicationUser>())
            .Returns(Microsoft.AspNetCore.Identity.IdentityResult.Success);
        userManager.AddToRoleAsync(Arg.Any<ApplicationUser>(), AppRoles.Client)
            .Returns(Microsoft.AspNetCore.Identity.IdentityResult.Success);
        userManager.GetRolesAsync(Arg.Any<ApplicationUser>()).Returns(["Client"]);

        var db = new MockDbBuilder()
            .With(MakeValidNonce())
            .Build();
        var config = MakeConfig();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(verifier, userManager, db, config, _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        // Act — no name in body (Apple omitted on first auth for this test)
        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "valid-token", Nonce = ValidNonce, HealthDataConsent = true },
            CancellationToken.None);

        ep.ValidationFailed.Should().BeFalse();

        // Name fields must be empty strings, not null
        await userManager.Received(1).CreateAsync(
            Arg.Is<ApplicationUser>(u =>
                u.FirstName == string.Empty &&
                u.LastName == string.Empty));

        // New user must be provisioned with the Client role — NOT Trainer.
        // Assigning Trainer here would allow any anonymous caller with a valid
        // Apple token to gain Trainer privileges (privilege escalation).
        await userManager.Received(1).AddToRoleAsync(Arg.Any<ApplicationUser>(), AppRoles.Client);
        await userManager.DidNotReceive().AddToRoleAsync(Arg.Any<ApplicationUser>(), AppRoles.Trainer);

        // A ClientProfile must have been created (mirroring RegisterEndpoint for Client role).
        db.ClientProfiles.Received(1).Add(Arg.Any<ClientProfile>());

        // ProfessionalProfile must NOT be created for a new Apple social-login user.
        db.ProfessionalProfiles.DidNotReceive().Add(Arg.Any<ProfessionalProfile>());
    }

    // ── Requested roles for new accounts ───────────────────────────────────

    private static Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> MakeNewUserManager()
    {
        var manager = EndpointTestHelpers.CreateFakeUserManager();
        manager.FindByEmailAsync(PrivateRelayPayload.Email!).Returns((ApplicationUser?)null);
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
        var ep = Factory.Create<AppleSocialLoginEndpoint>(MakeVerifier(PrivateRelayPayload), userManager, db, MakeConfig(), _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);
        ep.HttpContext.Request.Headers.AcceptLanguage = "cs";

        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "valid-token", Nonce = ValidNonce, Roles = ["Trainer"] },
            CancellationToken.None);

        ep.ValidationFailed.Should().BeFalse();
        ep.Response.AccessToken.Should().NotBeNullOrEmpty();
        await userManager.Received(1).AddToRoleAsync(Arg.Any<ApplicationUser>(), AppRoles.Trainer);
        await userManager.DidNotReceive().AddToRoleAsync(Arg.Any<ApplicationUser>(), AppRoles.Client);
        db.ProfessionalProfiles.Received(1).Add(Arg.Any<ProfessionalProfile>());
        db.ClientProfiles.DidNotReceive().Add(Arg.Any<ClientProfile>());
        db.Questionnaires.Received(1).Add(Arg.Is<Questionnaire>(q =>
            q.Type == FormUp.Application.Domain.Enums.QuestionnaireType.CheckIn && q.Title == "Týdenní check-in"));
        db.UserExternalLogins.Received(1).Add(Arg.Is<UserExternalLogin>(el => el.Provider == "apple"));
        await _inviteConversationSeeder.DidNotReceive().SeedForNewUserAsync(Arg.Any<ApplicationUser>(), Arg.Any<CancellationToken>());
        await _inviteNotificationSeeder.DidNotReceive().SeedForNewUserAsync(Arg.Any<ApplicationUser>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NewUserRequestsNutritionistWithoutLanguageHeader_FallsBackToEnglishForm()
    {
        var userManager = MakeNewUserManager();
        var db = new MockDbBuilder().With(MakeValidNonce()).Build();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(MakeVerifier(PrivateRelayPayload), userManager, db, MakeConfig(), _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "valid-token", Nonce = ValidNonce, Roles = ["nutritionist"] },
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
        var ep = Factory.Create<AppleSocialLoginEndpoint>(MakeVerifier(PrivateRelayPayload), userManager, db, MakeConfig(), _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "valid-token", Nonce = ValidNonce, HealthDataConsent = true },
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
        var user = EntityBuilder.User.WithId(userId).WithEmail(ValidPayloadWithEmail.Email!).Build();
        var externalLogin = new UserExternalLogin { UserId = userId, Provider = "apple", Subject = ValidPayloadWithEmail.Subject };
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(userId.ToString()).Returns(user);
        userManager.GetRolesAsync(user).Returns(["Client"]);
        var db = new MockDbBuilder().With(MakeValidNonce()).With(externalLogin).Build();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(MakeVerifier(ValidPayloadWithEmail), userManager, db, MakeConfig(), _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "valid-token", Nonce = ValidNonce, Roles = ["Trainer"] },
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
        var existingUser = EntityBuilder.User.WithEmail(PrivateRelayPayload.Email!).Build();
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByEmailAsync(PrivateRelayPayload.Email!).Returns(existingUser);
        var db = new MockDbBuilder().With(MakeValidNonce()).Build();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(MakeVerifier(PrivateRelayPayload), userManager, db, MakeConfig(), _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "valid-token", Nonce = ValidNonce, Roles = ["Trainer"] },
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
        var ep = Factory.Create<AppleSocialLoginEndpoint>(MakeVerifier(PrivateRelayPayload), userManager, db, MakeConfig(), _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await FluentActions
            .Awaiting(() => ep.HandleAsync(
                new AppleSocialLoginRequest { IdentityToken = "valid-token", Nonce = ValidNonce, HealthDataConsent = consent },
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
        var ep = Factory.Create<AppleSocialLoginEndpoint>(MakeVerifier(PrivateRelayPayload), userManager, db, MakeConfig(), _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "valid-token", Nonce = ValidNonce, HealthDataConsent = true },
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
            Arg.Is<string?>(v => v!.Contains("\"healthDataConsent\":true") && v.Contains("\"provider\":\"apple\"") && v.Contains("\"Client\"")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NewCoachWithoutConsent_LeavesConsentNullAndAudits()
    {
        var userManager = MakeNewUserManager();
        var db = new MockDbBuilder().With(MakeValidNonce()).Build();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(MakeVerifier(PrivateRelayPayload), userManager, db, MakeConfig(), _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "valid-token", Nonce = ValidNonce, Roles = ["Nutritionist"] },
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
            Arg.Is<string?>(v => v!.Contains("\"healthDataConsent\":null") && v.Contains("\"Nutritionist\"")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_LinkedClientWithoutConsent_LogsInAndWritesNoConsent()
    {
        var userId = Guid.NewGuid();
        var user = EntityBuilder.User.WithId(userId).WithEmail(ValidPayloadWithEmail.Email!).Build();
        var externalLogin = new UserExternalLogin { UserId = userId, Provider = "apple", Subject = ValidPayloadWithEmail.Subject };
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(userId.ToString()).Returns(user);
        userManager.GetRolesAsync(user).Returns(["Client"]);
        var db = new MockDbBuilder().With(MakeValidNonce()).With(externalLogin).Build();
        var ep = Factory.Create<AppleSocialLoginEndpoint>(MakeVerifier(ValidPayloadWithEmail), userManager, db, MakeConfig(), _inviteConversationSeeder, _inviteNotificationSeeder, _audit, _logger, _coachEvents);

        await ep.HandleAsync(
            new AppleSocialLoginRequest { IdentityToken = "valid-token", Nonce = ValidNonce },
            CancellationToken.None);

        ep.Response.AccessToken.Should().NotBeNullOrEmpty();
        user.HealthDataConsent.Should().BeNull();
        user.HealthDataConsentDate.Should().BeNull();
        await userManager.DidNotReceive().CreateAsync(Arg.Any<ApplicationUser>());
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default, default!, default!, default, default, default, default, default);
    }
}
