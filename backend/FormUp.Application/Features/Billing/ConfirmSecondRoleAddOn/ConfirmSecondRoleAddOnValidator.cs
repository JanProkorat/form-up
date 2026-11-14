using FastEndpoints;
using FluentValidation;

namespace FormUp.Application.Features.Billing.ConfirmSecondRoleAddOn;

/// <summary>
/// Validates the <see cref="ConfirmSecondRoleAddOnRequest"/>.
/// </summary>
internal sealed class ConfirmSecondRoleAddOnValidator : Validator<ConfirmSecondRoleAddOnRequest>
{
    /// <summary>
    /// Initializes validation rules for confirming the add-on.
    /// </summary>
    public ConfirmSecondRoleAddOnValidator()
    {
        RuleFor(x => x.AttemptId)
            .NotEmpty();
    }
}
