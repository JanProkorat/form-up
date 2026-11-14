namespace FormUp.Application.Features.AdminPromotions.EndPromotion;

/// <summary>
/// Request for ending a promotion early.
/// </summary>
public class EndPromotionRequest
{
    /// <summary>Route parameter — the promotion's public id; matches <c>{PromotionId}</c> in the route exactly.</summary>
    public Guid PromotionId { get; set; }
}
