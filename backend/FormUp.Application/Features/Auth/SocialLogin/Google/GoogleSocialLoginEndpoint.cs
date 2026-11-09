using System.Security.Cryptography;
using FastEndpoints;
using FastEndpoints.Security;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Auth.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FormUp.Application.Features.Auth.SocialLogin.Google;

/// <summary>
/// Endpoint for signing in via Google Identity Services.
/// Verifies the Google ID token, then either logs in an existing linked account,
/// provisions a new account from the Google profile, or returns 409 when the
/// email belongs to a password-only account with no Google link.
/// </summary>
public class GoogleSocialLoginEndpoint(
    IGoogleTokenVerifier googleVerifier,
    UserManager<ApplicationUser> userManager,
    IApplicationDbContext db,
    IConfiguration config,
    // Seeds a professional-client conversation against any message-bearing PendingInvite
    // already addressed to a newly-provisioned account's email (#803/#817).
    IPendingInviteConversationSeeder inviteConversationSeeder,
    IPendingInviteNotificationSeeder inviteNotificationSeeder,
    IAuditService audit,
    ILogger<GoogleSocialLoginEndpoint> logger) : Endpoint<GoogleSocialLoginRequest, GoogleSocialLoginResponse>
{
    private const string GoogleProvider = "google";

    /// <inheritdoc />
    public override void Configure()
    {
        Post("/auth/social/google");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(AppPolicies.AuthRateLimit));
        Summary(s =>
        {
            s.Summary = "Google social login";
            s.Description = "Verifies a Google ID token and returns platform JWT tokens. Provisions a new account if the email is not yet registered, with the requested roles (Trainer, Nutritionist or Client; default Client). Roles and healthDataConsent are ignored for an already-linked account. A new Client account requires healthDataConsent=true (same as /auth/register); a coach sign-up must omit it.";
            s.Response<GoogleSocialLoginResponse>(200, "Login successful");
            s.Response<ProblemDetails>(400, "idToken or nonce is missing or empty, Roles is invalid (Admin, unknown, or Client combined with a coach role), a coach sends healthDataConsent, or a new Client account lacks healthDataConsent=true. The nonce is already consumed by the time the consent check runs, so the client must request a new nonce to retry");
            s.Response(401, "Google ID token is invalid, expired, has wrong audience, or the nonce is invalid/consumed/expired");
            s.Response(403, "Account is deactivated");
            s.Response(409, "Email belongs to an existing password-only account (social_email_conflict)");
            s.Response(429, "Rate limit exceeded");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GoogleSocialLoginRequest req, CancellationToken ct)
    {
        // 1. Fast pre-check: reject obviously invalid nonces before paying for token verification.
        // This path is NOT the consume step — it is a cheap short-circuit for clearly-bad nonces.
        var nonceRecord = await db.SocialLoginNonces
            .FirstOrDefaultAsync(n => n.Nonce == req.Nonce, ct);

        if (nonceRecord is null || nonceRecord.ConsumedAt != null || nonceRecord.ExpiresAt < DateTime.UtcNow)
        {
            await this.SendProblemAsync(StatusCodes.Status401Unauthorized,
                ErrorCodes.InvalidCredentials,
                "Social sign-in nonce is invalid, already used, or has expired. Request a new nonce and retry.",
                ct);
            return;
        }

        // 2. Verify the Google ID token (throws on failure).
        // The verifier confirms the token's nonce field equals req.Nonce (raw, not hashed for Google).
        // Token verification happens BEFORE the atomic consume so that a bad token
        // does NOT burn the nonce — the client may retry with the same nonce if
        // the token itself was the problem (e.g. a transient clock skew).
        GoogleTokenPayload googlePayload;
        try
        {
            googlePayload = await googleVerifier.VerifyAsync(req.IdToken, req.Nonce, ct);
        }
        catch (InvalidOperationException)
        {
            await this.SendProblemAsync(StatusCodes.Status401Unauthorized,
                ErrorCodes.InvalidCredentials,
                "Google ID token is invalid, expired, or has the wrong audience.",
                ct);
            return;
        }

        // 3. Atomically consume the nonce with a single conditional UPDATE statement.
        // This closes the concurrent-request window: two parallel requests that both
        // passed the pre-check and both verified a valid token will race here; only
        // one UPDATE will find ConsumedAt == null, the other returns 0 rows affected.
        // The nonce is spent on every verified outcome (200, 409) — it must not be
        // replayable once the identity token has been accepted.
        var consumed = await db.ConsumeNonceAsync(req.Nonce, ct);
        if (consumed == 0)
        {
            // Lost the concurrent consume race, or the nonce expired between the
            // pre-check read and the UPDATE. Either way, deny as invalid.
            await this.SendProblemAsync(StatusCodes.Status401Unauthorized,
                ErrorCodes.InvalidCredentials,
                "Social sign-in nonce is invalid, already used, or has expired. Request a new nonce and retry.",
                ct);
            return;
        }

        // 2. Look up the UserExternalLogin row for (google, sub).
        var externalLogin = await db.UserExternalLogins
            .FirstOrDefaultAsync(
                el => el.Provider == GoogleProvider && el.Subject == googlePayload.Subject,
                ct);

        ApplicationUser user;

        if (externalLogin is not null)
        {
            // Happy path A: existing Google link found — load the linked user.
            var linkedUser = await userManager.FindByIdAsync(externalLogin.UserId.ToString());
            if (linkedUser is null)
            {
                // Orphaned external login — treat as invalid credentials.
                await this.SendProblemAsync(StatusCodes.Status401Unauthorized,
                    ErrorCodes.InvalidCredentials,
                    "Linked account not found.",
                    ct);
                return;
            }

            user = linkedUser;
        }
        else
        {
            // No Google link yet. Check by email.
            var existingUser = await userManager.FindByEmailAsync(googlePayload.Email);

            if (existingUser is not null)
            {
                // Email exists but no Google link — this is a password-only account.
                // Return 409 with social_email_conflict so the web prompts the user
                // to sign in with their password instead.
                await this.SendProblemAsync(StatusCodes.Status409Conflict,
                    ErrorCodes.SocialEmailConflict,
                    "An account with this email already exists. Please sign in with your password.",
                    ct);
                return;
            }

            // Happy path B: new user — provision from Google profile.
            var nameParts = (googlePayload.Name ?? googlePayload.Email).Split(' ', 2);
            var firstName = nameParts[0];
            var lastName = nameParts.Length > 1 ? nameParts[1] : string.Empty;

            // The validator limits requested roles to Trainer, Nutritionist or Client (never Admin,
            // never Client combined with a coach role); an empty list means Client. Roles are read
            // for new accounts only — a linked account keeps what it has.
            var newUserRoles = SocialSignUpRoles.Resolve(req.Roles);
            var isCoach = SocialSignUpRoles.HasCoachRole(newUserRoles);

            // Same semantics as /auth/register: a new Client account needs the Art. 9 health-data
            // consent. Checked here, not in the validator, because new-vs-linked is only known now.
            if (!isCoach && req.HealthDataConsent != true)
            {
                AddError(r => r.HealthDataConsent, SocialSignUpRoles.HealthDataConsentRequiredMessage);
                ThrowIfAnyErrors();
                return;
            }

            var nowUtc = DateTime.UtcNow;
            var newUser = new ApplicationUser
            {
                UserName = googlePayload.Email,
                Email = googlePayload.Email,
                EmailConfirmed = true, // Google has already verified the email.
                FirstName = firstName,
                LastName = lastName,
                GdprConsent = true,
                GdprConsentDate = nowUtc,
                // Art. 9 health-data consent: set for clients only; coaches stay null.
                HealthDataConsent = isCoach ? null : req.HealthDataConsent,
                HealthDataConsentDate = isCoach ? null : nowUtc
            };

            var createResult = await userManager.CreateAsync(newUser);
            if (!createResult.Succeeded)
            {
                foreach (var error in createResult.Errors)
                {
                    AddError(error.Description);
                }
                ThrowIfAnyErrors();
                return;
            }

            foreach (var role in newUserRoles)
            {
                await userManager.AddToRoleAsync(newUser, role);
            }

            // Role-specific profile, mirroring RegisterEndpoint.
            if (isCoach)
            {
                db.ProfessionalProfiles.Add(new ProfessionalProfile { UserId = newUser.Id });
            }
            else
            {
                db.ClientProfiles.Add(new ClientProfile { UserId = newUser.Id });
            }

            // Link the Google identity.
            db.UserExternalLogins.Add(new UserExternalLogin
            {
                UserId = newUser.Id,
                Provider = GoogleProvider,
                Subject = googlePayload.Subject,
                CreatedAt = DateTime.UtcNow
            });

            await db.SaveChangesAsync(ct);
            user = newUser;

            // Audit: consent recorded at sign-up, same entry as /auth/register plus the provider.
            var healthDataConsentValue = newUser.HealthDataConsent.HasValue
                ? (newUser.HealthDataConsent.Value ? "true" : "false")
                : "null";
            await audit.LogAsync(
                newUser.Id,
                "Register",
                nameof(ApplicationUser),
                newUser.Id,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                newValues: $"{{\"gdprConsent\":true,\"healthDataConsent\":{healthDataConsentValue},\"roles\":[{string.Join(",", newUserRoles.Select(role => $"\"{role}\""))}],\"provider\":\"{GoogleProvider}\"}}",
                ct: ct);

            if (isCoach)
            {
                var language = HttpContext.Request.Headers.AcceptLanguage.FirstOrDefault() ?? "en";
                await DefaultCheckInForm.EnsureAsync(db, newUser.Id, language, ct);
            }
            else
            {
                await SeedPendingInvitesAsync(newUser, ct);
            }
        }

        // 3. Check if the account is active.
        if (!user.IsActive)
        {
            await this.SendProblemAsync(StatusCodes.Status403Forbidden,
                ErrorCodes.AccountDeactivated,
                "Account is deactivated.",
                ct);
            return;
        }

        // 4. Issue tokens (same logic as LoginEndpoint).
        var roles = await userManager.GetRolesAsync(user);
        var expiresAt = DateTime.UtcNow.AddMinutes(
            config.GetValue(ConfigKeys.JwtAccessTokenExpirationMinutes, 15));

        var accessToken = JwtBearer.CreateToken(o =>
        {
            o.SigningKey = config[ConfigKeys.JwtSecret]!;
            o.ExpireAt = expiresAt;
            o.User.Roles.AddRange(roles);
            o.User.Claims.Add((AppClaims.UserId, user.Id.ToString()));
            o.User.Claims.Add((AppClaims.Email, user.Email!));
        });

        var refreshTokenValue = GenerateRefreshToken();
        var refreshTokenDays = config.GetValue(ConfigKeys.JwtRefreshTokenExpirationDays, 7);

        db.RefreshTokens.Add(new Domain.Entities.RefreshToken
        {
            UserId = user.Id,
            Token = refreshTokenValue,
            ExpiresAt = DateTime.UtcNow.AddDays(refreshTokenDays)
        });

        await db.SaveChangesAsync(ct);

        await Send.OkAsync(new GoogleSocialLoginResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshTokenValue,
            ExpiresAt = expiresAt,
            EmailConfirmed = user.EmailConfirmed
        }, ct);
    }

    /// <summary>
    /// For a new Client account that matches an existing PendingInvite, seeds the professional-client
    /// conversation and the in-app notification now instead of waiting for the client to accept.
    /// Non-fatal: the account and its Google link are already committed, so a seed failure must not
    /// turn a successful sign-in into a 500 — accept-time seeding remains the fallback.
    /// </summary>
    private async Task SeedPendingInvitesAsync(ApplicationUser newUser, CancellationToken ct)
    {
        try
        {
            await inviteConversationSeeder.SeedForNewUserAsync(newUser, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Failed to seed pending-invite conversation(s) for {Email} during Google sign-up. User {UserId} created; conversation will still be seeded at invite-accept time.",
                newUser.Email, newUser.Id);
        }

        try
        {
            await inviteNotificationSeeder.SeedForNewUserAsync(newUser, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Failed to create pending-invite notification(s) during Google sign-up for user {UserId}.",
                newUser.Id);
        }
    }

    /// <summary>
    /// Generates a cryptographically secure random refresh token string.
    /// </summary>
    private static string GenerateRefreshToken()
    {
        var randomBytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(randomBytes);
    }
}
