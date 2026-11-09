using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace FormUp.Application.Features.Auth.RequestPasswordReset;

/// <summary>
/// Endpoint for requesting a password reset. Generates a reset token and
/// sends it via email. Returns 404 when no account uses the email.
/// </summary>
/// <param name="userManager">ASP.NET Identity user manager.</param>
/// <param name="emailService">Email service for sending password reset emails.</param>
/// <param name="logger">Logger instance.</param>
public class RequestPasswordResetEndpoint(
    UserManager<ApplicationUser> userManager,
    IEmailService emailService,
    ILogger<RequestPasswordResetEndpoint> logger) : Endpoint<RequestPasswordResetRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/auth/password/reset");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(AppPolicies.AuthRateLimit));
        Summary(s =>
        {
            s.Summary = "Request password reset";
            s.Description = "Sends a password reset link to the specified email. Returns 404 EMAIL_NOT_REGISTERED when no account uses it.";
            s.Response(StatusCodes.Status200OK, "Reset link sent (or send failure logged)");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,"Invalid request");
            s.Response(StatusCodes.Status404NotFound, "No account uses this email");
            s.Response(StatusCodes.Status429TooManyRequests, "Rate limit exceeded");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(RequestPasswordResetRequest req, CancellationToken ct)
    {
        var user = await userManager.FindByEmailAsync(req.Email);

        if (user is null)
        {
            await this.SendProblemAsync(404, ErrorCodes.EmailNotRegistered, "No account uses this email.", ct);
            return;
        }

        try
        {
            var token = await userManager.GeneratePasswordResetTokenAsync(user);

            var language = HttpContext.Request.Headers.AcceptLanguage.FirstOrDefault() ?? "en";
            await emailService.SendPasswordResetEmailAsync(req.Email, token, language, ct);

            logger.LogInformation(
                "Password reset email sent to {Email}",
                req.Email);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Non-fatal: the token was already issued and the request itself is valid,
            // so a provider outage is an operational problem, not a client error.
            // Logged at Error because the user will not receive the email and this
            // log line is the only signal that it happened.
            logger.LogError(ex,
                "Failed to send password reset email to {Email}.",
                req.Email);
        }

        await Send.OkAsync(new { Message = "A password reset link has been sent." }, ct);
    }
}
