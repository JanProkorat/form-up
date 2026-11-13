namespace FormUp.Application.Domain.Enums;

/// <summary>
/// What a lapsed coach may still do to a plan of a client who is over the Free plan's cap.
/// </summary>
public enum OverCapEditAccess
{
    /// <summary>The client is not over the cap; no restriction applies.</summary>
    Unrestricted,

    /// <summary>The client is over the cap but the plan's last published week has not ended: content edits are allowed.</summary>
    WindowOpen,

    /// <summary>The action was refused and the 403 response has been written.</summary>
    Refused,
}
