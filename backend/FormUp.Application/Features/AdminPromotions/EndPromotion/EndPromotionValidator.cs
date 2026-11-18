using FastEndpoints;
using FluentValidation;
using FormUp.Application.Domain.Constants;

namespace FormUp.Application.Features.AdminPromotions.EndPromotion;

/// <summary>
/// Validates the <see cref="EndPromotionRequest"/>.
/// </summary>
internal sealed class EndPromotionValidator : Validator<EndPromotionRequest>
{
    /// <summary>
    /// Initializes validation rules for ending a promotion.
    /// </summary>
    public EndPromotionValidator()
    {
        RuleFor(x => x.PromotionId)
            .NotEmpty().WithErrorCode(ErrorCodes.Required);
    }
}
