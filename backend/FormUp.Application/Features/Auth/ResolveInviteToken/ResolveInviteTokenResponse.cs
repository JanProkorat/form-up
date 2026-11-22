namespace FormUp.Application.Features.Auth.ResolveInviteToken;

/// <summary>
/// The email an invitation token was issued for.
/// </summary>
public class ResolveInviteTokenResponse
{
    /// <summary>
    /// Invited email address.
    /// </summary>
    public string Email { get; set; } = string.Empty;
}
