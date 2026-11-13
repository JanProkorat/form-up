using FastEndpoints;
using FluentValidation;
using FormUp.Application.Domain.Constants;

namespace FormUp.Application.Features.CoachSubscriptions.SetCoachSubscription;

/// <summary>
/// Validates the <see cref="SetCoachSubscriptionRequest"/>. Plan and coach existence are endpoint-level checks.
/// </summary>
internal sealed class SetCoachSubscriptionValidator : Validator<SetCoachSubscriptionRequest>
{
    /// <summary>
    /// Initializes validation rules for assigning a coach subscription.
    /// </summary>
    public SetCoachSubscriptionValidator()
    {
        RuleFor(x => x.CoachId)
            .NotEmpty().WithErrorCode(ErrorCodes.Required);

        RuleFor(x => x.PlanCode)
            .NotEmpty().WithErrorCode(ErrorCodes.Required)
            .Matches("^[a-z0-9-]{1,50}$").WithErrorCode(ErrorCodes.OutOfRange)
            .WithMessage("Plan code must be 1-50 lowercase letters, digits, or hyphens.");

        RuleFor(x => x.Status)
            .IsInEnum().WithErrorCode(ErrorCodes.OutOfRange);
    }
}
