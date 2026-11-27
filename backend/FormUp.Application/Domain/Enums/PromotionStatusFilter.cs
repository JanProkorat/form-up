namespace FormUp.Application.Domain.Enums;

/// <summary>
/// The tab of the admin Promotions list.
/// </summary>
public enum PromotionStatusFilter
{
    /// <summary>Every promotion.</summary>
    All,

    /// <summary>Claimable now.</summary>
    Active,

    /// <summary>Claim window not open yet.</summary>
    Scheduled,

    /// <summary>Claim window closed or every use taken.</summary>
    Ended
}
