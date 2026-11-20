using FastEndpoints;
using FluentValidation;
using FormUp.Application.Domain.Constants;

namespace FormUp.Application.Features.AdminReports.GetAdminReports;

/// <summary>
/// Validates the <see cref="GetAdminReportsRequest"/>.
/// </summary>
internal sealed class GetAdminReportsValidator : Validator<GetAdminReportsRequest>
{
    /// <summary>
    /// Initializes validation rules for the admin reports query.
    /// </summary>
    public GetAdminReportsValidator()
    {
        RuleFor(x => x.Period)
            .IsInEnum().WithErrorCode(ErrorCodes.OutOfRange);
    }
}
