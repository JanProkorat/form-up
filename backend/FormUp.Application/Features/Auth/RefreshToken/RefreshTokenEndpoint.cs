using System.Security.Cryptography;
using FastEndpoints;
using FastEndpoints.Security;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Auth.RefreshToken;

/// <summary>
/// Endpoint for rotating a refresh token and issuing a new access + refresh token pair.
/// The old refresh token is invalidated upon use.
/// </summary>
/// <remarks>
/// Rotation is closed against concurrent reuse with an atomic conditional update
/// (<c>WHERE Token = @token AND RevokedAt IS NULL</c>) rather than a read-then-write.
/// The conditional update and the successor row insert run in a single database
/// transaction (<see cref="IApplicationDbContext.RotateRefreshTokenAsync"/>, #694) —
/// so the successor row is always durably present whenever <c>ReplacedByToken</c>
/// is set, never left dangling by a crash between two separate writes.
/// Exactly one concurrent caller can win that update; the loser re-reads the
/// now-revoked row and is routed into a grace-window discriminator (Auth0-style
/// rotation-with-reuse-detection):
/// <list type="bullet">
/// <item>Revoked, no successor recorded (plain logout) → reject. Never theft.</item>
/// <item>Revoked, successor recorded, within the grace window → benign reconcile:
/// mint a fresh access token and return the already-issued successor. No family
/// revocation — this is the legitimate concurrent double-fire path.</item>
/// <item>Revoked, successor recorded, outside the grace window → genuine reuse:
/// revoke every active token for the user (the whole family) and reject.</item>
/// </list>
/// </remarks>
/// <param name="db">Database context.</param>
/// <param name="userManager">ASP.NET Identity user manager.</param>
/// <param name="config">Application configuration.</param>
public class RefreshTokenEndpoint(
    IApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    IConfiguration config) : Endpoint<RefreshTokenRequest, RefreshTokenResponse>
{
    private const int DefaultGraceWindowSeconds = 20;

    /// <inheritdoc />
    public override void Configure()
    {
        Post("/auth/refresh");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(AppPolicies.RefreshRateLimit));
        Summary(s =>
        {
            s.Summary = "Refresh access token";
            s.Description = "Exchanges a valid refresh token for a new access + refresh token pair. The old refresh token is invalidated.";
            s.Response<RefreshTokenResponse>(StatusCodes.Status200OK, "New access and refresh token pair");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid input, or an invalid, expired or reused refresh token");
            s.Response(StatusCodes.Status429TooManyRequests, "Rate limit exceeded");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(RefreshTokenRequest req, CancellationToken ct)
    {
        var existingToken = await db.RefreshTokens
            .AsNoTracking()
            .Include(rt => rt.User)
            .FirstOrDefaultAsync(rt => rt.Token == req.RefreshToken, ct);

        if (existingToken is null)
        {
            ThrowError("Invalid or expired refresh token.");
            return;
        }

        if (existingToken.IsRevoked)
        {
            await HandleRevokedTokenAsync(existingToken, ct);
            return;
        }

        if (existingToken.IsExpired)
        {
            ThrowError("Invalid or expired refresh token.");
            return;
        }

        var user = existingToken.User;

        if (!user.IsActive)
        {
            ThrowError("Account is deactivated.");
            return;
        }

        var newRefreshTokenValue = GenerateRefreshToken();
        var rotatedAt = DateTime.UtcNow;
        var refreshTokenDays = config.GetValue(ConfigKeys.JwtRefreshTokenExpirationDays, 7);

        var successorToken = new Domain.Entities.RefreshToken
        {
            UserId = user.Id,
            Token = newRefreshTokenValue,
            ExpiresAt = DateTime.UtcNow.AddDays(refreshTokenDays)
        };

        // Atomic conditional update + successor insert, in a single transaction
        // (#694) — exactly one concurrent caller can win this, and the successor
        // row is guaranteed durably present whenever ReplacedByToken is set.
        var rowsAffected = await db.RotateRefreshTokenAsync(req.RefreshToken, successorToken, rotatedAt, ct);

        if (rowsAffected == 0)
        {
            // Lost the rotation race: another request already revoked this token
            // between our read and our conditional update. Re-read the
            // authoritative row and route into the reuse/reconcile discriminator.
            var raced = await db.RefreshTokens
                .AsNoTracking()
                .Include(rt => rt.User)
                .FirstOrDefaultAsync(rt => rt.Token == req.RefreshToken, ct);

            if (raced is null || !raced.IsRevoked)
            {
                ThrowError("Invalid or expired refresh token.");
                return;
            }

            await HandleRevokedTokenAsync(raced, ct);
            return;
        }

        // We won the rotation race — the successor row is already durably
        // persisted (RotateRefreshTokenAsync inserted it in the same
        // transaction as the conditional update). Just issue the new token pair.
        var roles = await userManager.GetRolesAsync(user);
        var expiresAt = DateTime.UtcNow.AddMinutes(
            config.GetValue(ConfigKeys.JwtAccessTokenExpirationMinutes, 15));
        var accessToken = CreateAccessToken(user, roles, expiresAt);

        await Send.OkAsync(new RefreshTokenResponse
        {
            AccessToken = accessToken,
            RefreshToken = newRefreshTokenValue,
            ExpiresAt = expiresAt
        }, ct);
    }

    /// <summary>
    /// Discriminates between a plain logout-revoked token, a benign concurrent
    /// double-fire (grace-window reconcile), and genuine reuse/theft.
    /// </summary>
    private async Task HandleRevokedTokenAsync(Domain.Entities.RefreshToken token, CancellationToken ct)
    {
        if (token.ReplacedByToken is null)
        {
            // Plain logout-revoked token — never treat as theft. Otherwise a
            // normal logout could nuke every other active session.
            ThrowError("Invalid or expired refresh token.");
            return;
        }

        var graceWindowSeconds = config.GetValue(ConfigKeys.RefreshTokenReuseGraceWindowSeconds, DefaultGraceWindowSeconds);
        var withinGraceWindow = DateTime.UtcNow - token.RevokedAt!.Value <= TimeSpan.FromSeconds(graceWindowSeconds);

        if (withinGraceWindow)
        {
            // Benign concurrent double-fire (e.g. a client retry racing its own
            // successful request): reissue a fresh access token bound to the
            // already-minted successor. Do NOT revoke the family — this is the
            // legitimate concurrent path, not theft.
            var user = token.User;

            if (!user.IsActive)
            {
                ThrowError("Account is deactivated.");
                return;
            }

            var roles = await userManager.GetRolesAsync(user);
            var expiresAt = DateTime.UtcNow.AddMinutes(
                config.GetValue(ConfigKeys.JwtAccessTokenExpirationMinutes, 15));
            var accessToken = CreateAccessToken(user, roles, expiresAt);

            await Send.OkAsync(new RefreshTokenResponse
            {
                AccessToken = accessToken,
                RefreshToken = token.ReplacedByToken,
                ExpiresAt = expiresAt
            }, ct);
            return;
        }

        // Outside the grace window: genuine reuse/theft. Revoke every active
        // token for this user so the whole family is invalidated, then reject.
        await db.RevokeRefreshTokenFamilyAsync(token.UserId, DateTime.UtcNow, ct);
        ThrowError("Invalid or expired refresh token.");
    }

    /// <summary>
    /// Creates a signed JWT access token for the given user.
    /// </summary>
    private string CreateAccessToken(ApplicationUser user, IList<string> roles, DateTime expiresAt) =>
        JwtBearer.CreateToken(o =>
        {
            o.SigningKey = config[ConfigKeys.JwtSecret]!;
            o.ExpireAt = expiresAt;
            o.User.Roles.AddRange(roles);
            o.User.Claims.Add((AppClaims.UserId, user.Id.ToString()));
            o.User.Claims.Add((AppClaims.Email, user.Email!));
        });

    /// <summary>
    /// Generates a cryptographically secure random refresh token string.
    /// </summary>
    /// <returns>A base64-encoded random token.</returns>
    private static string GenerateRefreshToken()
    {
        var randomBytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(randomBytes);
    }
}
