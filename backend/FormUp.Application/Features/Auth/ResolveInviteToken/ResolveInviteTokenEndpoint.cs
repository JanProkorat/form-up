using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;

namespace FormUp.Application.Features.Auth.ResolveInviteToken;

/// <summary>
/// Resolves an invitation token to the invited email so the sign-up page can lock the email field.
/// </summary>
/// <param name="dbContext">Database context.</param>
/// <param name="timeProvider">Clock.</param>
public class ResolveInviteTokenEndpoint(IApplicationDbContext dbContext, TimeProvider timeProvider)
    : Endpoint<ResolveInviteTokenRequest, ResolveInviteTokenResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/auth/invite/resolve");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(AppPolicies.InviteResolveRateLimit));
        Summary(s =>
        {
            s.Summary = "Resolve an invitation token to the invited email";
            s.Description = "Anonymous because the invitee has no account yet; the token is the credential. "
                + "Unknown, expired and used tokens all return the same bare 404.";
            s.Response<ResolveInviteTokenResponse>(200, "Invited email");
            s.Response<ProblemDetails>(400, "Validation error");
            s.Response(404, "Token unknown, expired or already used");
            s.Response(429, "Rate limit exceeded");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(ResolveInviteTokenRequest req, CancellationToken ct)
    {
        var email = await InvitationTokenLookup.FindValidEmailAsync(
            dbContext, req.Token, timeProvider.GetUtcNow().UtcDateTime, ct);

        if (email is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(new ResolveInviteTokenResponse { Email = email }, ct);
    }
}
