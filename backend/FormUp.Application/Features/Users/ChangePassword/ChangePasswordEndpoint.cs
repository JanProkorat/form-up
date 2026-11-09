using System.Security.Claims;
using System.Security.Cryptography;
using FastEndpoints;
using FastEndpoints.Security;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;

namespace FormUp.Application.Features.Users.ChangePassword;

/// <summary>
/// Changes the authenticated user's password, revokes every refresh token and returns a fresh token pair.
/// </summary>
/// <param name="userManager">ASP.NET Identity user manager.</param>
/// <param name="db">Database context, used for the transaction and refresh tokens.</param>
/// <param name="config">Application configuration for JWT settings.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="timeProvider">Clock.</param>
public class ChangePasswordEndpoint(
    UserManager<ApplicationUser> userManager,
    IApplicationDbContext db,
    IConfiguration config,
    IAuditService audit,
    TimeProvider timeProvider) : Endpoint<ChangePasswordRequest, ChangePasswordResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/users/me/password");
        Options(x => x.RequireRateLimiting(AppPolicies.AuthRateLimit));
        Summary(s =>
        {
            s.Summary = "Change password";
            s.Description = "Changes the caller's password. Revokes all refresh tokens and returns a fresh access and refresh token pair for the current session.";
            s.Response<ChangePasswordResponse>(StatusCodes.Status200OK, "Password changed; fresh tokens returned");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid input, wrong current password (INVALID_CURRENT_PASSWORD), or account has no password (PASSWORD_NOT_SET)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status404NotFound, "User no longer exists");
            s.Response(StatusCodes.Status429TooManyRequests, "Rate limit exceeded");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(ChangePasswordRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var user = await userManager.FindByIdAsync(userId);

        if (user is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (user.PasswordHash is null)
        {
            this.ThrowErrorWithCode(ErrorCodes.PasswordNotSet, "This account has no password.");
            return;
        }

        // Password change, revoke and new token must commit together: Identity's store and
        // db share one scoped context, so all writes enlist in this transaction.
        await using var transaction = await db.BeginTransactionAsync(ct);

        var result = await userManager.ChangePasswordAsync(user, req.CurrentPassword, req.NewPassword);

        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch)))
            {
                this.ThrowErrorWithCode(ErrorCodes.InvalidCurrentPassword, "Current password is incorrect.");
                return;
            }

            ThrowError(string.Join(" ", result.Errors.Select(e => e.Description)));
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        user.PasswordChangedAt = now;
        await userManager.UpdateAsync(user);
        await db.RevokeRefreshTokenFamilyAsync(user.Id, now, ct);

        var roles = await userManager.GetRolesAsync(user);
        var expiresAt = now.AddMinutes(config.GetValue(ConfigKeys.JwtAccessTokenExpirationMinutes, 15));

        var accessToken = JwtBearer.CreateToken(o =>
        {
            o.SigningKey = config[ConfigKeys.JwtSecret]!;
            o.ExpireAt = expiresAt;
            o.User.Roles.AddRange(roles);
            o.User.Claims.Add((AppClaims.UserId, user.Id.ToString()));
            o.User.Claims.Add((AppClaims.Email, user.Email!));
        });

        var refreshTokenValue = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

        db.RefreshTokens.Add(new Domain.Entities.RefreshToken
        {
            UserId = user.Id,
            Token = refreshTokenValue,
            ExpiresAt = now.AddDays(config.GetValue(ConfigKeys.JwtRefreshTokenExpirationDays, 7))
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // Audit entry deliberately carries no password values.
        await audit.LogAsync(
            user.Id,
            "ChangePassword",
            nameof(ApplicationUser),
            user.Id,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        await Send.OkAsync(new ChangePasswordResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshTokenValue,
            ExpiresAt = expiresAt,
            PasswordChangedAt = now
        }, ct);
    }
}
