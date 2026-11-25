using FormUp.Application.Features.Auth.ChangeUnverifiedEmail;
using FormUp.Application.Features.Auth.Logout;
using FormUp.Application.Features.Auth.ResendVerification;
using FormUp.Application.Features.Users.GetProfile;

namespace FormUp.Application.Infrastructure.EmailVerification;

/// <summary>
/// The one list of authenticated endpoints an account with an unconfirmed email may still call. Login, token refresh
/// and email verification are anonymous, so they need no entry. Every other authenticated endpoint is refused by
/// <see cref="EmailVerificationGateMiddleware"/>. The list is pinned by an architecture test; add to it only on purpose.
/// </summary>
public static class EmailVerificationExemptions
{
    /// <summary>Endpoint types that pass the gate.</summary>
    public static IReadOnlySet<Type> Endpoints { get; } = new HashSet<Type>
    {
        typeof(LogoutEndpoint),
        typeof(ResendVerificationEndpoint),
        typeof(ChangeUnverifiedEmailEndpoint),
        typeof(GetProfileEndpoint),
    };
}
