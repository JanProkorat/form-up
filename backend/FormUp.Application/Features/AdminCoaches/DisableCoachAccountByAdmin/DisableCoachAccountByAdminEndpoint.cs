using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.AdminCoaches.DisableCoachAccountByAdmin;

/// <summary>
/// Disables a coach's account on an admin's behalf, with the same effect as the coach's own Disable button.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="disabler">Disables the account; shared with the coach's own action.</param>
internal sealed class DisableCoachAccountByAdminEndpoint(IApplicationDbContext db, CoachAccountDisabler disabler)
    : Endpoint<DisableCoachAccountByAdminRequest, DisableCoachAccountByAdminResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/admin/coaches/{CoachId}/disable");
        Roles(AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "Disable a coach's account";
            s.Description = "Marks the coach account as not renewing. Roles are removed at the end of the paid period, or at once when there " +
                            "is none; repeating the call while pending returns the same date. Writes a history entry with the admin and the reason.";
            s.Response<DisableCoachAccountByAdminResponse>(StatusCodes.Status200OK, "Date the account stops being active and the roles removed by this call");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid request body, or the coach has no active coach role (NO_ACTIVE_COACH_ROLE)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "Caller is not an Admin");
            s.Response(StatusCodes.Status404NotFound, "The coach is unknown (PROFESSIONAL_NOT_FOUND)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(DisableCoachAccountByAdminRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(AppClaims.UserId), out var adminId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (!await db.ProfessionalProfiles.AsNoTracking().AnyAsync(p => p.UserId == req.CoachId, ct))
        {
            await this.SendProblemAsync(
                StatusCodes.Status404NotFound, ErrorCodes.ProfessionalNotFound, "Coach not found.", ct);
            return;
        }

        var result = await disabler.DisableAsync(
            req.CoachId, adminId, req.Reason.Trim(), HttpContext.Connection.RemoteIpAddress?.ToString(), ct);

        if (result is null)
        {
            this.ThrowErrorWithCode(ErrorCodes.NoActiveCoachRole, "The coach has no active coach role.");
            return;
        }

        await Send.OkAsync(new DisableCoachAccountByAdminResponse
        {
            CoachId = req.CoachId,
            ActiveUntil = result.ActiveUntil,
            RolesRemoved = result.RolesRemoved.ToList(),
        }, ct);
    }
}
