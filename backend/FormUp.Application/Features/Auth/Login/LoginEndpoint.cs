using System.Security.Cryptography;
using FastEndpoints;
using FastEndpoints.Security;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;

namespace FormUp.Application.Features.Auth.Login;

/// <summary>
/// Endpoint for authenticating a user and issuing JWT access + refresh tokens.
/// </summary>
/// <param name="userManager">ASP.NET Identity user manager.</param>
/// <param name="db">Database context for storing refresh tokens.</param>
/// <param name="config">Application configuration for JWT settings.</param>
public class LoginEndpoint(
    UserManager<ApplicationUser> userManager,
    IApplicationDbContext db,
    IConfiguration config) : Endpoint<LoginRequest, LoginResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/auth/login");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(AppPolicies.AuthRateLimit));
        Summary(s =>
        {
            s.Summary = "Authenticate user";
            s.Description = "Validates credentials and returns a JWT access token (15 min) and refresh token (7 days).";
            s.Response<LoginResponse>(StatusCodes.Status200OK, "Login successful");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid input, wrong credentials (INVALID_CREDENTIALS) or a deactivated account (ACCOUNT_DEACTIVATED)");
            s.Response(StatusCodes.Status429TooManyRequests, "Rate limit exceeded");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(LoginRequest req, CancellationToken ct)
    {
        var user = await userManager.FindByEmailAsync(req.Email);

        if (user is null || !await userManager.CheckPasswordAsync(user, req.Password))
        {
            this.ThrowErrorWithCode(ErrorCodes.InvalidCredentials, "Invalid email or password.");
            return;
        }

        if (!user.IsActive)
        {
            this.ThrowErrorWithCode(ErrorCodes.AccountDeactivated, "Account is deactivated.");
            return;
        }

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

        var refreshToken = new Domain.Entities.RefreshToken
        {
            UserId = user.Id,
            Token = refreshTokenValue,
            ExpiresAt = DateTime.UtcNow.AddDays(refreshTokenDays)
        };

        db.RefreshTokens.Add(refreshToken);
        await db.SaveChangesAsync(ct);

        await Send.OkAsync(new LoginResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshTokenValue,
            ExpiresAt = expiresAt,
            EmailConfirmed = user.EmailConfirmed
        }, ct);
    }

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
