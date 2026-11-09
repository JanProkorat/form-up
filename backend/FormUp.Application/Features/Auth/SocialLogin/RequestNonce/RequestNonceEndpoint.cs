using System.Security.Cryptography;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Infrastructure.Data;

namespace FormUp.Application.Features.Auth.SocialLogin.RequestNonce;

/// <summary>
/// Issues a cryptographically random, single-use nonce for use in Apple or Google social sign-in.
/// The client embeds the nonce in the sign-in flow (Apple: SHA-256 of the raw value; Google: raw value),
/// then presents the raw nonce back together with the resulting identity token so the backend can
/// verify the token's embedded nonce claim and reject replays.
/// </summary>
public class RequestNonceEndpoint(IApplicationDbContext db) : EndpointWithoutRequest<RequestNonceResponse>
{
    /// <summary>
    /// Nonce time-to-live. 10 minutes is generous enough for a normal sign-in flow
    /// while short enough to limit the replay window.
    /// </summary>
    private static readonly TimeSpan NonceTtl = TimeSpan.FromMinutes(10);

    /// <inheritdoc />
    public override void Configure()
    {
        Post("/auth/social/nonce");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(AppPolicies.AuthRateLimit));
        Summary(s =>
        {
            s.Summary = "Request a social sign-in nonce";
            s.Description = "Issues a single-use nonce (valid for 10 minutes) for embedding in an Apple or Google sign-in flow. The nonce must be presented together with the resulting identity token.";
            s.Response<RequestNonceResponse>(200, "Nonce issued successfully");
            s.Response(429, "Rate limit exceeded");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
    {
        // Generate a cryptographically random nonce — 32 bytes encoded as base64url (no padding).
        var rawBytes = RandomNumberGenerator.GetBytes(32);
        var nonce = Convert.ToBase64String(rawBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        var nonceRecord = new SocialLoginNonce
        {
            Nonce = nonce,
            ExpiresAt = DateTime.UtcNow.Add(NonceTtl),
            CreatedAt = DateTime.UtcNow
        };

        db.SocialLoginNonces.Add(nonceRecord);
        await db.SaveChangesAsync(ct);

        await Send.OkAsync(new RequestNonceResponse { Nonce = nonce }, ct);
    }
}
