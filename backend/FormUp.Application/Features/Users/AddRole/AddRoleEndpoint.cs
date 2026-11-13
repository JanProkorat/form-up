using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Cryptography;
using FastEndpoints;
using FastEndpoints.Security;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Users.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Users.AddRole;

/// <summary>
/// Endpoint that allows a Trainer or Nutritionist to add the other professional role to their account.
/// </summary>
/// <param name="userManager">ASP.NET Identity user manager.</param>
/// <param name="db">Database context.</param>
/// <param name="config">Application configuration for JWT settings.</param>
/// <param name="audit">Audit logging service.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Exempt, Reason = "Adds or restores a coach role, so it must work when no role is active.")]
public class AddRoleEndpoint(
    UserManager<ApplicationUser> userManager,
    IApplicationDbContext db,
    IConfiguration config,
    IAuditService audit) : Endpoint<AddRoleRequest, AddRoleResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/users/me/roles");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Add a professional role";
            s.Description = "Allows a Trainer to also become a Nutritionist, or vice versa. Returns fresh tokens with updated roles.";
            s.Response<AddRoleResponse>(StatusCodes.Status200OK, "The role was added; fresh tokens with the updated roles");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,
                "Invalid caller identity, the role is not self-assignable, or the caller already has the role (ROLE_ALREADY_ASSIGNED)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden,
                "The subscription does not cover a second professional role (SUBSCRIPTION_REQUIRED)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(AddRoleRequest req, CancellationToken ct)
    {
        var userIdString = User.FindFirst(AppClaims.UserId)?.Value;
        if (userIdString is null || !Guid.TryParse(userIdString, out var userId))
        {
            ThrowError("Invalid user identity.");
            return;
        }

        // Defense-in-depth: handler-level guard mirrors AddRoleValidator so that
        // a validator bypass (wiring change, validator replacement) cannot re-open
        // the Admin self-promotion path. Both this guard and the validator must be
        // widened simultaneously — that's the invariant. See SelfAssignableRoles
        // for the rationale and issue #308.
        if (!SelfAssignableRoles.Contains(req.Role))
        {
            ThrowError(r => r.Role, "Role is not self-assignable.", 400);
            return;
        }

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            ThrowError("User not found.");
            return;
        }

        var currentRoles = await userManager.GetRolesAsync(user);
        if (currentRoles.Contains(req.Role))
        {
            // A held-but-removed role is restored instead of rejected.
            var existingProfile = await db.ProfessionalProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);
            if (!CoachRoleStatus.IsRemoved(existingProfile, req.Role))
            {
                this.ThrowErrorWithCode(ErrorCodes.RoleAlreadyAssigned, "You already have this role.");
                return;
            }

            // Restoring a role is a second role only while the other coach role is still active; restoring the
            // only role after a full removal or disable is not.
            var otherRoleActive = CoachRoleStatus.ActiveRoles(currentRoles, existingProfile)
                .Any(role => role != req.Role);

            if (otherRoleActive && await this.RespondIfSecondRoleNotAvailableAsync(db, userId, ct))
            {
                return;
            }
        }
        else
        {
            // Every caller already holds one professional role, so a new one is the second role.
            if (await this.RespondIfSecondRoleNotAvailableAsync(db, userId, ct))
            {
                return;
            }

            await userManager.AddToRoleAsync(user, req.Role);
        }

        // Adding a role reactivates the coach account: clear the removal marker and any pending disable.
        // Create the ProfessionalProfile when the user has none yet (Nutritionist → Trainer case).
        var profile = await db.ProfessionalProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is null)
        {
            db.ProfessionalProfiles.Add(new ProfessionalProfile { UserId = userId });
        }
        else
        {
            CoachRoleStatus.SetRemovedAt(profile, req.Role, null);

            // The cancel flag is only ever set together with the disable date.
            if (profile.CoachAccountActiveUntil is not null)
            {
                profile.CoachAccountActiveUntil = null;

                var subscription = await db.CoachSubscriptions
                    .FirstOrDefaultAsync(cs => cs.ProfessionalProfileId == profile.Id, ct);

                if (subscription is not null)
                {
                    subscription.CancelAtPeriodEnd = false;
                }
            }
        }

        await db.SaveChangesAsync(ct);

        if (profile is null)
        {
            await DefaultCheckInForm.EnsureAsync(db, userId, user.Language, ct);
        }

        // Audit log
        await audit.LogAsync(
            userId,
            "AddRole",
            nameof(ApplicationUser),
            userId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            newValues: $"{{\"addedRole\":\"{req.Role}\"}}",
            ct: ct);

        // Generate fresh tokens with updated roles
        var updatedRoles = await userManager.GetRolesAsync(user);
        var expiresAt = DateTime.UtcNow.AddMinutes(
            config.GetValue(ConfigKeys.JwtAccessTokenExpirationMinutes, 15));

        var accessToken = JwtBearer.CreateToken(o =>
        {
            o.SigningKey = config[ConfigKeys.JwtSecret]!;
            o.ExpireAt = expiresAt;
            o.User.Roles.AddRange(updatedRoles);
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

        await Send.OkAsync(new AddRoleResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshTokenValue,
            ExpiresAt = expiresAt,
            AddedRole = req.Role
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
