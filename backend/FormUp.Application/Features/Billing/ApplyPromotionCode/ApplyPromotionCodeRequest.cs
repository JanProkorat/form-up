namespace FormUp.Application.Features.Billing.ApplyPromotionCode;

/// <summary>
/// Request for applying a promotion code to the caller's own live subscription.
/// </summary>
public class ApplyPromotionCodeRequest
{
    /// <summary>The code as the coach typed it. Letters, digits, hyphen and underscore only.</summary>
    public string Code { get; set; } = string.Empty;
}
