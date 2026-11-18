namespace FormUp.Application.Domain.Enums;

/// <summary>
/// Where a <see cref="Entities.Promotion"/> stands now. Never stored; derived from the claim window and the uses.
/// </summary>
public enum PromotionStatus
{
    /// <summary>Claimable now.</summary>
    Active,

    /// <summary>The claim window has not opened yet.</summary>
    Scheduled,

    /// <summary>The claim window closed, or every use is taken.</summary>
    Ended
}
