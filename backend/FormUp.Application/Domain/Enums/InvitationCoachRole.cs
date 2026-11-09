namespace FormUp.Application.Domain.Enums;

/// <summary>
/// The coaching discipline(s) an invitation email names under the inviting coach's name.
/// </summary>
public enum InvitationCoachRole
{
    /// <summary>The invitation grants training plans only.</summary>
    Trainer,

    /// <summary>The invitation grants nutrition plans only.</summary>
    Nutritionist,

    /// <summary>The invitation grants both training and nutrition plans.</summary>
    Both
}
