using FastEndpoints;
using FluentValidation;
using FormUp.Application.Domain.Constants;

namespace FormUp.Application.Features.AdminCoaches.ListAdminCoaches;

/// <summary>
/// Validates the <see cref="ListAdminCoachesRequest"/>.
/// </summary>
internal sealed class ListAdminCoachesValidator : Validator<ListAdminCoachesRequest>
{
    /// <summary>
    /// Initializes validation rules for listing coaches.
    /// </summary>
    public ListAdminCoachesValidator()
    {
        RuleFor(x => x.Page)
            .GreaterThanOrEqualTo(1).WithErrorCode(ErrorCodes.OutOfRange);

        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, 100).WithErrorCode(ErrorCodes.OutOfRange);

        RuleFor(x => x.Status)
            .IsInEnum().WithErrorCode(ErrorCodes.OutOfRange);

        RuleFor(x => x.Search)
            .MaximumLength(100);
    }
}
