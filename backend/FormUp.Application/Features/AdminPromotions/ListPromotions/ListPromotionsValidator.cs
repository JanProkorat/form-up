using FastEndpoints;
using FluentValidation;

namespace FormUp.Application.Features.AdminPromotions.ListPromotions;

/// <summary>
/// Validates the <see cref="ListPromotionsRequest"/>.
/// </summary>
internal sealed class ListPromotionsValidator : Validator<ListPromotionsRequest>
{
    /// <summary>
    /// Initializes validation rules for listing promotions.
    /// </summary>
    public ListPromotionsValidator()
    {
        RuleFor(x => x.Status).IsInEnum();
    }
}
