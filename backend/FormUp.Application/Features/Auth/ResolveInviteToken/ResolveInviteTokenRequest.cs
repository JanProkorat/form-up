namespace FormUp.Application.Features.Auth.ResolveInviteToken;

/// <summary>
/// Request for resolving an invitation token to the invited email.
/// </summary>
public class ResolveInviteTokenRequest
{
    /// <summary>
    /// The invitation token from the invite link.
    /// </summary>
    public string Token { get; set; } = string.Empty;
}
