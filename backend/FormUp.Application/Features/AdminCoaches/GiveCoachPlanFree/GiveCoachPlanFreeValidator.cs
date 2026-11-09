using FastEndpoints;
using FluentValidation;
using FormUp.Application.Domain.Constants;

namespace FormUp.Application.Features.AdminCoaches.GiveCoachPlanFree;

/// <summary>
/// Validates the <see cref="GiveCoachPlanFreeRequest"/>. Coach and plan existence are endpoint-level checks.
/// </summary>
internal sealed class GiveCoachPlanFreeValidator : Validator<GiveCoachPlanFreeRequest>
{
    /// <summary>Longest accepted reason, so the audit row's values column always fits.</summary>
    public const int MaxReasonLength = 300;

    /// <summary>
    /// Initializes validation rules for giving a plan for free.
    /// </summary>
    public GiveCoachPlanFreeValidator()
    {
        RuleFor(x => x.CoachId)
            .NotEmpty().WithErrorCode(ErrorCodes.Required);

        RuleFor(x => x.PlanCode)
            .NotEmpty().WithErrorCode(ErrorCodes.Required)
            .Matches("^[a-z0-9-]{1,50}$").WithErrorCode(ErrorCodes.OutOfRange)
            .WithMessage("Plan code must be 1-50 lowercase letters, digits, or hyphens.")
            .NotEqual(SubscriptionPlanCodes.Free).WithErrorCode(ErrorCodes.OutOfRange)
            .WithMessage("The Free plan cannot be given for free.");

        RuleFor(x => x.FreeUntil)
            .Must(freeUntil => freeUntil!.Value > DateTimeOffset.UtcNow).WithErrorCode(ErrorCodes.OutOfRange)
            .WithMessage("The end of the gift must be in the future.")
            .When(x => x.FreeUntil.HasValue);

        RuleFor(x => x.Reason)
            .NotEmpty().WithErrorCode(ErrorCodes.Required)
            .MaximumLength(MaxReasonLength).WithErrorCode(ErrorCodes.OutOfRange);
    }
}
