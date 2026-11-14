namespace FormUp.Application.Features.AdminPromotions.Shared;

/// <summary>
/// Audit action names written by the admin promotion actions (each at most 50 characters). Every row is filed under the
/// promotion's public id.
/// </summary>
internal static class AdminPromotionAuditActions
{
    /// <summary>An admin created a promotion.</summary>
    public const string Create = "AdminCreatePromotion";
}
