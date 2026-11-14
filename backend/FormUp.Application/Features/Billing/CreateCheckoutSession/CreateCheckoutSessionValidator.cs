using FastEndpoints;
using FluentValidation;

namespace FormUp.Application.Features.Billing.CreateCheckoutSession;

/// <summary>
/// Validates the <see cref="CreateCheckoutSessionRequest"/>.
/// </summary>
internal sealed class CreateCheckoutSessionValidator : Validator<CreateCheckoutSessionRequest>
{
    /// <summary>
    /// Initializes validation rules for starting a checkout.
    /// </summary>
    public CreateCheckoutSessionValidator()
    {
        RuleFor(x => x.PlanCode)
            .NotEmpty()
            .MaximumLength(100);
    }
}
