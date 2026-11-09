using System.Security.Claims;
using System.Text.Json;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace FormUp.Application.Features.Auth.ChangeUnverifiedEmail;

/// <summary>
/// Lets a signed-in user whose email is still unverified correct the address and receive a new
/// verification email. The change counts toward the lifetime verification-email cap.
/// </summary>
/// <param name="userManager">ASP.NET Identity user manager.</param>
/// <param name="tokenService">Shared email-verification token issuance service.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="logger">Logger for non-fatal send failures.</param>
public class ChangeUnverifiedEmailEndpoint(
    UserManager<ApplicationUser> userManager,
    IEmailVerificationTokenService tokenService,
    IAuditService audit,
    ILogger<ChangeUnverifiedEmailEndpoint> logger)
    : Endpoint<ChangeUnverifiedEmailRequest, ChangeUnverifiedEmailResponse>
{
    private const int MaxVerificationEmails = 4;

    /// <inheritdoc />
    public override void Configure()
    {
        Put("/auth/email");
        Options(x => x.RequireRateLimiting(AppPolicies.AuthRateLimit));
        Summary(s =>
        {
            s.Summary = "Change the email of an unverified account";
            s.Description = "Replaces the email address of the caller's unverified account, invalidates "
                            + "earlier verification tokens and sends a new one. Any authenticated role.";
            s.Response<ChangeUnverifiedEmailResponse>(StatusCodes.Status200OK, "Email changed, verification email sent");
            s.Response<ProblemDetails>(
                StatusCodes.Status400BadRequest,
                "Invalid address, already verified, unchanged, in use, or resend limit reached");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status404NotFound, "Caller's account no longer exists");
            s.Response(StatusCodes.Status429TooManyRequests, "Rate limit exceeded");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(ChangeUnverifiedEmailRequest req, CancellationToken ct)
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

        var newEmail = req.NewEmail.Trim();
        var oldEmail = user.Email;

        await EnsureChangeAllowedAsync(user, newEmail);

        user.Email = newEmail;
        user.UserName = newEmail;

        var result = await userManager.UpdateAsync(user);

        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail)
                                           or nameof(IdentityErrorDescriber.DuplicateUserName)))
            {
                this.ThrowErrorWithCode(ErrorCodes.EmailAlreadyInUse, "Email address is already in use.");
                return;
            }

            foreach (var error in result.Errors)
            {
                AddError(error.Description);
            }

            ThrowIfAnyErrors();
        }

        var language = HttpContext.Request.Headers.AcceptLanguage.FirstOrDefault() ?? "en";

        try
        {
            await tokenService.IssueAndSendAsync(user, language, ct, countTowardLifetimeCap: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Failed to send verification email after an email change for user {UserId}; they can request a resend.",
                user.Id);
        }

        await audit.LogAsync(
            user.Id,
            "ChangeUnverifiedEmail",
            nameof(ApplicationUser),
            user.Id,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            oldValues: JsonSerializer.Serialize(new { email = oldEmail }),
            newValues: JsonSerializer.Serialize(new { email = newEmail }),
            ct: ct);

        await Send.OkAsync(new ChangeUnverifiedEmailResponse
        {
            Email = newEmail,
            RemainingResends = Math.Max(0, MaxVerificationEmails - user.VerificationEmailsSent),
        }, ct);
    }

    private async Task EnsureChangeAllowedAsync(ApplicationUser user, string newEmail)
    {
        if (user.EmailConfirmed)
        {
            this.ThrowErrorWithCode(ErrorCodes.EmailAlreadyVerified, "Email is already verified.");
        }

        if (string.Equals(user.Email, newEmail, StringComparison.OrdinalIgnoreCase))
        {
            this.ThrowErrorWithCode(ErrorCodes.EmailUnchanged, "The new email equals the current one.");
        }

        var existing = await userManager.FindByEmailAsync(newEmail);

        if (existing is not null && existing.Id != user.Id)
        {
            this.ThrowErrorWithCode(ErrorCodes.EmailAlreadyInUse, "Email address is already in use.");
        }

        if (user.VerificationEmailsSent >= MaxVerificationEmails)
        {
            this.ThrowErrorWithCode(
                ErrorCodes.VerificationResendLimitReached, "Maximum number of verification emails reached.");
        }
    }
}
