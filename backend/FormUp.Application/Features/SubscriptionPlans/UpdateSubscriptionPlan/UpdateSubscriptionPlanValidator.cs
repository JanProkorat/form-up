using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FluentValidation;

namespace FormUp.Application.Features.SubscriptionPlans.UpdateSubscriptionPlan;

/// <summary>
/// Validates the <see cref="UpdateSubscriptionPlanRequest"/>. <c>Code</c> is bound from the
/// route and is not an updatable field, but its shape is still validated here — the route no
/// longer carries a regex constraint (see
/// <see cref="UpdateSubscriptionPlanEndpoint"/>), so the wire-format check moved entirely
/// into this validator.
/// </summary>
internal sealed class UpdateSubscriptionPlanValidator : Validator<UpdateSubscriptionPlanRequest>
{
    /// <summary>
    /// Initializes validation rules for subscription plan updates.
    /// </summary>
    public UpdateSubscriptionPlanValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty().WithErrorCode(ErrorCodes.Required)
            .MaximumLength(50).WithErrorCode(ErrorCodes.OutOfRange)
            .Matches("^[a-z0-9-]+$").WithErrorCode(ErrorCodes.OutOfRange)
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

        RuleFor(x => x.BothRolesIncluded)
            .NotNull().WithErrorCode(ErrorCodes.Required);

        RuleFor(x => x.SecondRoleAddOnPriceMinorUnits)
            .GreaterThan(0).WithErrorCode(ErrorCodes.OutOfRange)
            .When(x => x.SecondRoleAddOnPriceMinorUnits.HasValue);

        RuleFor(x => x)
            .Must(x => x.BothRolesIncluded == !x.SecondRoleAddOnPriceMinorUnits.HasValue)
            .WithErrorCode(ErrorCodes.SecondRoleOptionInvalid)
            .WithMessage("Set exactly one of bothRolesIncluded and secondRoleAddOnPriceMinorUnits.")
            .When(x => x.BothRolesIncluded.HasValue);

        RuleFor(x => x.CanCreatePlans)
            .NotNull().WithErrorCode(ErrorCodes.Required);

        RuleFor(x => x.CanMessage)
            .NotNull().WithErrorCode(ErrorCodes.Required);

        RuleFor(x => x.CanSendQuestionnaires)
            .NotNull().WithErrorCode(ErrorCodes.Required);

        RuleFor(x => x.CanUseWeeklyCheckIns)
            .NotNull().WithErrorCode(ErrorCodes.Required);

        RuleFor(x => x.CanUsePerClientCheckInConfig)
            .NotNull().WithErrorCode(ErrorCodes.Required);

        RuleFor(x => x.CanUsePayments)
            .NotNull().WithErrorCode(ErrorCodes.Required);

        RuleFor(x => x.IsActive)
            .NotNull().WithErrorCode(ErrorCodes.Required);

        RuleFor(x => x.BillingInterval)
            .IsInEnum().WithErrorCode(ErrorCodes.OutOfRange);

        RuleFor(x => x.Currency)
            .Matches("^[A-Z]{3}$").WithErrorCode(ErrorCodes.OutOfRange)
            .Must(SupportedCurrencies.All.Contains).WithErrorCode(ErrorCodes.UnsupportedCurrency)
            .WithMessage($"Currency must be one of: {string.Join(", ", SupportedCurrencies.All)}.");

        RuleFor(x => x.PriceMinorUnits)
            .GreaterThanOrEqualTo(0).WithErrorCode(ErrorCodes.OutOfRange);

        RuleFor(x => x.MaxActiveClients)
            .Must(field => field.IsSet).WithErrorCode(ErrorCodes.Required)
            .WithMessage("maxActiveClients is required (pass null for unlimited).");

        RuleFor(x => x.MaxActiveClients.Value)
            .GreaterThanOrEqualTo(1).WithErrorCode(ErrorCodes.OutOfRange)
            .When(x => x.MaxActiveClients.IsSet && x.MaxActiveClients.Value.HasValue);

        RuleFor(x => x.ExternalPriceId)
            .MaximumLength(200).WithErrorCode(ErrorCodes.OutOfRange);
    }
}
