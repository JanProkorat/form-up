using FastEndpoints;
using FluentValidation;
using FormUp.Application.Domain.Constants;

namespace FormUp.Application.Features.Billing.ApplyPromotionCode;

/// <summary>
/// Validates the <see cref="ApplyPromotionCodeRequest"/>.
/// </summary>
internal sealed class ApplyPromotionCodeValidator : Validator<ApplyPromotionCodeRequest>
{
    /// <summary>The longest code accepted.</summary>
    public const int MaxCodeLength = 50;

    /// <summary>
    /// Initializes validation rules for applying a promotion code.
    /// </summary>
    public ApplyPromotionCodeValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty()
            .WithErrorCode(ErrorCodes.PromotionCodeInvalid)
            .WithMessage("The promotion code is required.")
            .MaximumLength(MaxCodeLength)
            .WithErrorCode(ErrorCodes.PromotionCodeInvalid)
            .WithMessage("The promotion code is too long.")
            .Matches(@"^[A-Za-z0-9_-]+\z")
            .WithErrorCode(ErrorCodes.PromotionCodeInvalid)
            .WithMessage("The promotion code may only contain letters, digits, hyphen and underscore.");
    }
}
