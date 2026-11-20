using FastEndpoints;
using FluentValidation;
using FormUp.Application.Domain.Constants;

namespace FormUp.Application.Features.Billing.CreateCheckoutSession;

/// <summary>
/// Validates the <see cref="CreateCheckoutSessionRequest"/>.
/// </summary>
internal sealed class CreateCheckoutSessionValidator : Validator<CreateCheckoutSessionRequest>
{
    /// <summary>The longest code accepted.</summary>
    public const int MaxCodeLength = 50;

    /// <summary>
    /// Initializes validation rules for starting a checkout.
    /// </summary>
    public CreateCheckoutSessionValidator()
    {
        RuleFor(x => x.PlanCode)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.BillingInterval)
            .IsInEnum();

        RuleFor(x => x.Flow)
            .IsInEnum();

        RuleFor(x => x.Code)
            .MaximumLength(MaxCodeLength)
            .WithErrorCode(ErrorCodes.PromotionCodeInvalid)
            .WithMessage("The promotion code is too long.")
            .Matches(@"^[A-Za-z0-9_-]+\z")
            .WithErrorCode(ErrorCodes.PromotionCodeInvalid)
            .WithMessage("The promotion code may only contain letters, digits, hyphen and underscore.")
            .When(x => !string.IsNullOrEmpty(x.Code));
    }
}
