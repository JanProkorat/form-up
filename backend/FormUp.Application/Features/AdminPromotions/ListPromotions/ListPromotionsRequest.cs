using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.AdminPromotions.ListPromotions;

/// <summary>
/// Request for the admin Promotions list.
/// </summary>
public class ListPromotionsRequest
{
    /// <summary>Tab to show; All when omitted.</summary>
    public PromotionStatusFilter Status { get; set; } = PromotionStatusFilter.All;
}
