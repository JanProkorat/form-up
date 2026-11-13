using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FluentValidation;

namespace FormUp.Application.Features.SubscriptionPlans.CreateSubscriptionPlan;

/// <summary>
/// Validates the <see cref="CreateSubscriptionPlanRequest"/>. Code uniqueness is an
/// endpoint-level check — validators are constructed without DI and cannot query the
/// DbContext — see <see cref="CreateSubscriptionPlanEndpoint"/>.
/// </summary>
internal sealed class CreateSubscriptionPlanValidator : Validator<CreateSubscriptionPlanRequest>
{
    /// <summary>
    /// Initializes validation rules for subscription plan creation.
    /// </summary>
    public CreateSubscriptionPlanValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty().WithErrorCode(ErrorCodes.Required)
            .Matches("^[a-z0-9-]{1,50}$").WithErrorCode(ErrorCodes.OutOfRange)
            .WithMessage("Code must be 1-50 lowercase letters, digits, or hyphens.");

        RuleFor(x => x.NameCs)
            .NotEmpty().WithErrorCode(ErrorCodes.Required)
            .MaximumLength(200).WithErrorCode(ErrorCodes.OutOfRange);

        RuleFor(x => x.NameEn)
            .NotEmpty().WithErrorCode(ErrorCodes.Required)
            .MaximumLength(200).WithErrorCode(ErrorCodes.OutOfRange);

        RuleFor(x => x.NameDe)
            .NotEmpty().WithErrorCode(ErrorCodes.Required)
            .MaximumLength(200).WithErrorCode(ErrorCodes.OutOfRange);

        RuleFor(x => x.SecondRoleAddOnPriceMinorUnits)
            .GreaterThan(0).WithErrorCode(ErrorCodes.OutOfRange)
            .When(x => x.SecondRoleAddOnPriceMinorUnits.HasValue);

        RuleFor(x => x)
            .Must(x => x.BothRolesIncluded != x.SecondRoleAddOnPriceMinorUnits.HasValue)
            .WithErrorCode(ErrorCodes.SecondRoleOptionInvalid)
            .WithMessage("Set exactly one of bothRolesIncluded and secondRoleAddOnPriceMinorUnits.");

        RuleFor(x => x.BillingInterval)
            .IsInEnum().WithErrorCode(ErrorCodes.OutOfRange);

        RuleFor(x => x.Currency)
            .Matches("^[A-Z]{3}$").WithErrorCode(ErrorCodes.OutOfRange)
            .Must(SupportedCurrencies.All.Contains).WithErrorCode(ErrorCodes.UnsupportedCurrency)
            .WithMessage($"Currency must be one of: {string.Join(", ", SupportedCurrencies.All)}.");

        RuleFor(x => x.PriceMinorUnits)
            .GreaterThanOrEqualTo(0).WithErrorCode(ErrorCodes.OutOfRange);

        RuleFor(x => x.MaxActiveClients)
            .GreaterThanOrEqualTo(1).WithErrorCode(ErrorCodes.OutOfRange)
            .When(x => x.MaxActiveClients.HasValue);

        RuleFor(x => x.ExternalPriceId)
            .MaximumLength(200).WithErrorCode(ErrorCodes.OutOfRange);
    }
}
