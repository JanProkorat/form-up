using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Users.EmailLinks.Shared;
using FormUp.Application.Infrastructure.Data;

namespace FormUp.Application.Features.Users.EmailLinks.OneClick;

/// <summary>
/// Handles the RFC 8058 one-click unsubscribe post (<c>List-Unsubscribe=One-Click</c>) that a mail client sends to
/// the link in the <c>List-Unsubscribe</c> header. The signed token is the credential, so the route is anonymous.
/// Only POST exists: a GET from a mail scanner's prefetch never changes anything.
/// </summary>
/// <param name="tokens">Notification email link token service.</param>
/// <param name="db">Application database context.</param>
/// <param name="logger">Logger. Never receives the token or the email address.</param>
internal sealed class EmailLinkOneClickEndpoint(
    IEmailTurnOffTokenService tokens,
    IApplicationDbContext db,
    ILogger<EmailLinkOneClickEndpoint> logger)
    : Endpoint<EmailLinkOneClickRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/users/email-links/one-click");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(AppPolicies.EmailLinkRateLimit));
        Summary(s =>
        {
            s.Summary = "One-click unsubscribe from a notification email";
            s.Description = "Anonymous: the signed token in the query string is the credential. Accepts the RFC 8058 form post and sets only that event's email switch to off; the push choice is left alone. A valid token succeeds even without the List-Unsubscribe=One-Click field. Invalid links answer 400 with no body.";
            s.Response(StatusCodes.Status200OK, "The email is off");
            s.Response(StatusCodes.Status400BadRequest, "Tampered, expired or unknown link, or the account no longer exists; no body");
            s.Response(StatusCodes.Status429TooManyRequests, "Rate limit exceeded");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(EmailLinkOneClickRequest req, CancellationToken ct)
    {
        var outcome = await EmailLinkPreferenceSwitch.TrySetAsync(tokens, db, req.Token, emailEnabled: false, ct);

        if (outcome is null)
        {
            await Send.ResultAsync(TypedResults.BadRequest());
            return;
        }

        logger.LogInformation(
            "Notification email {Event} turned off by one-click post for user {UserId}", outcome.Event, outcome.UserId);

        await Send.ResultAsync(TypedResults.Ok());
    }
}
