using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Users.EmailLinks.Shared;
using FormUp.Application.Infrastructure.Data;

namespace FormUp.Application.Features.Users.EmailLinks.TurnOff;

/// <summary>
/// Turns off one notification email from the link in that email. The signed token is the credential, so the
/// route is anonymous: the person following the link may not be signed in.
/// </summary>
/// <param name="tokens">Notification email link token service.</param>
/// <param name="db">Application database context.</param>
/// <param name="logger">Logger. Never receives the token or the email address.</param>
internal sealed class EmailLinkTurnOffEndpoint(
    IEmailTurnOffTokenService tokens,
    IApplicationDbContext db,
    ILogger<EmailLinkTurnOffEndpoint> logger)
    : Endpoint<EmailLinkTurnOffRequest, EmailLinkTurnOffResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/users/email-links/turn-off");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(AppPolicies.EmailLinkRateLimit));
        Summary(s =>
        {
            s.Summary = "Turn off a notification email from its link";
            s.Description = "Anonymous: the signed token from the emailed link is the credential. Sets only the email switch of that event to off and leaves the push choice alone. Repeating it changes nothing.";
            s.Response<EmailLinkTurnOffResponse>(StatusCodes.Status200OK, "The email is off; returns the account email and the event");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Tampered, expired or unknown link, or the account no longer exists (EMAIL_LINK_INVALID)");
            s.Response(StatusCodes.Status429TooManyRequests, "Rate limit exceeded");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(EmailLinkTurnOffRequest req, CancellationToken ct)
    {
        var outcome = await EmailLinkPreferenceSwitch.TrySetAsync(tokens, db, req.Token, emailEnabled: false, ct);

        if (outcome is null)
        {
            this.ThrowErrorWithCode(ErrorCodes.EmailLinkInvalid, EmailLinkPreferenceSwitch.InvalidMessage);
            return;
        }

        logger.LogInformation(
            "Notification email {Event} turned off by link for user {UserId}", outcome.Event, outcome.UserId);

        await Send.OkAsync(EmailLinkTurnOffResponse.FromOutcome(outcome), ct);
    }
}
