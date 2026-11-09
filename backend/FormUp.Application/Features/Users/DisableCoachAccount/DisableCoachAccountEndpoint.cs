using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Services;

namespace FormUp.Application.Features.Users.DisableCoachAccount;

/// <summary>
/// Disables the caller's coach account: it stays active until the paid period ends, or ends at once
/// when there is no paid period.
/// </summary>
/// <param name="disabler">Disables the account; shared with the admin action.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Exempt, Reason = "Account lifecycle action that must stay reachable after every role is removed.")]
internal sealed class DisableCoachAccountEndpoint(CoachAccountDisabler disabler) : EndpointWithoutRequest<DisableCoachAccountResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/users/me/coach-account/disable");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Disable my coach account";
            s.Description = "Marks the coach account as not renewing. Roles are removed at the end of the paid period, or at once when there is none. Repeating the call while pending returns the same date.";
            s.Responses[StatusCodes.Status200OK] = "Date the account stops being active and the roles removed by this call";
            s.Responses[StatusCodes.Status400BadRequest] = "NO_ACTIVE_COACH_ROLE";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or unreadable caller claim";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(AppClaims.UserId), out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var result = await disabler.DisableAsync(
            userId, userId, reason: null, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);

        if (result is null)
        {
            this.ThrowErrorWithCode(ErrorCodes.NoActiveCoachRole, "You have no active coach role.");
            return;
        }

        await Send.OkAsync(new DisableCoachAccountResponse
        {
            ActiveUntil = result.ActiveUntil,
            RolesRemoved = result.RolesRemoved.ToList(),
        }, ct);
    }
}
