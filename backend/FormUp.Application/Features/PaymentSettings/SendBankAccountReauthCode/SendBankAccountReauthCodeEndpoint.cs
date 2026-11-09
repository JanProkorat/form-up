using System.Security.Claims;
using System.Security.Cryptography;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.PaymentSettings.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.PaymentSettings.SendBankAccountReauthCode;

/// <summary>
/// Emails a one-time code to a coach who has no password, to confirm a bank account change.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="userManager">ASP.NET Identity user manager.</param>
/// <param name="entitlementService">Resolves subscription entitlements.</param>
/// <param name="emailService">Sends the code.</param>
/// <param name="timeProvider">Clock.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Exempt, Reason = "The coach's own bank account stays editable while no coach role is active.")]
internal sealed class SendBankAccountReauthCodeEndpoint(
    IApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    EntitlementService entitlementService,
    IEmailService emailService,
    TimeProvider timeProvider)
    : EndpointWithoutRequest
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    /// <inheritdoc />
    public override void Configure()
    {
        Post("/payments/bank-account/code");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Options(x => x.RequireRateLimiting(AppPolicies.AuthRateLimit));
        Summary(s =>
        {
            s.Summary = "Email a bank account confirmation code";
            s.Description = "For an account with no password (social sign-in): emails a 10-minute one-time code that replaces the password when changing the bank account. Replaces any earlier unused code.";
            s.Response(StatusCodes.Status204NoContent, "Code emailed");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "The account has a password and uses it instead (REAUTH_CODE_NOT_AVAILABLE)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The subscription does not include payments (PAYMENTS_NOT_ENTITLED)");
            s.Response(StatusCodes.Status404NotFound, "The caller has no professional profile or account");
            s.Response(StatusCodes.Status429TooManyRequests, "Rate limit exceeded");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(AppClaims.UserId), out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var profileId = await this.LoadEntitledProfileIdOrRespondAsync(db, entitlementService, userId, ct);

        if (profileId is null)
        {
            return;
        }

        var user = await userManager.FindByIdAsync(userId.ToString());

        if (user is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (user.PasswordHash is not null)
        {
            this.ThrowErrorWithCode(ErrorCodes.ReauthCodeNotAvailable, "This account has a password; use it to confirm the change.");
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

        // A new code replaces every earlier unused one, so only the latest email works.
        await db.CoachReauthCodes
            .Where(c => c.UserId == userId && c.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now), ct);

        db.CoachReauthCodes.Add(new CoachReauthCode
        {
            UserId = userId,
            CodeHash = PaymentSecrets.HashCode(userId, code),
            ExpiresAt = now.Add(CodeLifetime)
        });
        await db.SaveChangesAsync(ct);

        await emailService.SendReauthCodeEmailAsync(user.Email!, code, user.Language ?? "en", ct);

        await Send.NoContentAsync(ct);
    }
}
