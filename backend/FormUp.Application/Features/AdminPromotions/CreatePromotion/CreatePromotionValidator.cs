using FastEndpoints;
using FluentValidation;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.AdminPromotions.CreatePromotion;

/// <summary>
/// Validates the <see cref="CreatePromotionRequest"/>.
/// </summary>
internal sealed class CreatePromotionValidator : Validator<CreatePromotionRequest>
{
    /// <summary>The longest name accepted.</summary>
    public const int MaxNameLength = 200;

    /// <summary>The longest code accepted.</summary>
    public const int MaxCodeLength = 50;

    /// <summary>The only currency a fixed amount may use.</summary>
    public const string FixedAmountCurrency = "CZK";

    /// <summary>The fewest months of "several months".</summary>
    public const int MinMonths = 2;

    /// <summary>The most months of "several months".</summary>
    public const int MaxMonths = 36;

    /// <summary>
    /// Initializes validation rules for creating a promotion.
    /// </summary>
    public CreatePromotionValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(MaxNameLength);

        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Mode).IsInEnum();
        RuleFor(x => x.Duration).IsInEnum();

        RuleFor(x => x.Value)
            .InclusiveBetween(1, 100)
            .WithErrorCode(ErrorCodes.PromotionValueInvalid)
            .WithMessage("A percentage must be between 1 and 100.")
            .When(x => x.Kind == PromotionKind.Percent);

        RuleFor(x => x.Value)
            .GreaterThan(0)
            .WithErrorCode(ErrorCodes.PromotionValueInvalid)
            .WithMessage("A fixed amount must be positive.")
            .When(x => x.Kind == PromotionKind.FixedAmount);

        RuleFor(x => x.Currency)
            .Must(currency => string.Equals(currency, FixedAmountCurrency, StringComparison.OrdinalIgnoreCase))
            .WithErrorCode(ErrorCodes.PromotionValueInvalid)
            .WithMessage("A fixed amount is in CZK.")
            .When(x => x.Kind == PromotionKind.FixedAmount);

        RuleFor(x => x.Code)
            .NotEmpty()
            .WithErrorCode(ErrorCodes.PromotionModeInvalid)
            .WithMessage("A code is required when coaches claim the promotion with a code.")
            .When(x => x.Mode == PromotionMode.Code);

        RuleFor(x => x.Code)
            .MaximumLength(MaxCodeLength)
            .WithErrorCode(ErrorCodes.PromotionCodeInvalid)
            .WithMessage("The code is too long.")
            .Matches(@"^[A-Za-z0-9]+\z")
            .WithErrorCode(ErrorCodes.PromotionCodeInvalid)
            .WithMessage("The code may only contain letters and digits.")
            .When(x => x.Mode == PromotionMode.Code && !string.IsNullOrEmpty(x.Code));

        RuleFor(x => x.Code)
            .Must(code => string.IsNullOrEmpty(code))
            .WithErrorCode(ErrorCodes.PromotionModeInvalid)
            .WithMessage("An automatic promotion has no code.")
            .When(x => x.Mode == PromotionMode.Automatic);

        // WithErrorCode applies to the rule just before it, so the null check and the range check each carry their own.
        RuleFor(x => x.DurationMonths)
            .NotNull()
            .WithErrorCode(ErrorCodes.PromotionMonthsInvalid)
            .WithMessage("Several months needs a month count from 2 to 36.")
            .InclusiveBetween(MinMonths, MaxMonths)
            .WithErrorCode(ErrorCodes.PromotionMonthsInvalid)
            .WithMessage("Several months needs a month count from 2 to 36.")
            .When(x => x.Duration == PromotionDuration.Months);

        RuleFor(x => x.DurationMonths)
            .Null()
            .WithErrorCode(ErrorCodes.PromotionMonthsInvalid)
            .WithMessage("A month count only goes with several months.")
            .When(x => x.Duration != PromotionDuration.Months);

        RuleFor(x => x.ClaimUntil)
            .Must((request, until) => until > request.ClaimFrom)
            .WithErrorCode(ErrorCodes.PromotionWindowInvalid)
            .WithMessage("The claim window must end after it starts.")
            .Must(until => until > DateTimeOffset.UtcNow)
            .WithErrorCode(ErrorCodes.PromotionWindowInvalid)
            .WithMessage("The claim window must end in the future.")
            .When(x => x.ClaimUntil.HasValue);

        RuleFor(x => x.PlanCodes)
            .NotEmpty()
            .WithErrorCode(ErrorCodes.PromotionPlansInvalid)
            .WithMessage("Select at least one plan.");

        RuleForEach(x => x.PlanCodes)
            .NotEmpty()
            .MaximumLength(50)
            .WithErrorCode(ErrorCodes.PromotionPlansInvalid)
            .WithMessage("A plan code is empty or too long.");

        RuleFor(x => x.MaxUses)
            .GreaterThanOrEqualTo(1)
            .WithErrorCode(ErrorCodes.PromotionMaxUsesInvalid)
            .WithMessage("Max uses must be at least 1.")
            .When(x => x.MaxUses.HasValue);
    }
}
