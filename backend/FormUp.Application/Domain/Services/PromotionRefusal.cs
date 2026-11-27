namespace FormUp.Application.Domain.Services;

/// <summary>
/// Why a coach may not use a promotion: the machine-readable error code and a short English detail. Always a 409.
/// </summary>
/// <param name="ErrorCode">One of the <c>PROMOTION_CODE_*</c> error codes.</param>
/// <param name="Detail">Human-readable explanation, never containing the code text.</param>
public sealed record PromotionRefusal(string ErrorCode, string Detail);
