using FormUp.Application.Features.AdminPromotions.Shared;

namespace FormUp.Application.Features.AdminPromotions.ListPromotions;

/// <summary>
/// The promotions of one admin list tab.
/// </summary>
public class ListPromotionsResponse
{
    /// <summary>Promotions matching the tab, newest claim window first.</summary>
    public List<PromotionDto> Items { get; set; } = [];
}
