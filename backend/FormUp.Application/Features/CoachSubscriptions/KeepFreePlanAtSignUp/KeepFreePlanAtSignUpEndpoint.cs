using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.CoachSubscriptions.KeepFreePlanAtSignUp;

/// <summary>
/// Records that a coach kept the Free plan at sign-up, so the admin funnel counts the choice. It never changes the
/// subscription.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="timeProvider">Clock used to tell whether a given plan is still running.</param>
/// <param name="coachEvents">Adds the plan-choice event in this request's save.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
internal sealed class KeepFreePlanAtSignUpEndpoint(IApplicationDbContext db, TimeProvider timeProvider, ICoachEventRecorder coachEvents)
    : EndpointWithoutRequest
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/users/me/subscription/keep-free");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Record that the Free plan was kept at sign-up";
            s.Description = "Writes one plan-choice event for a coach on the Free plan (including a coach with no subscription row). " +
                            "Repeating the call changes nothing. The subscription is never touched.";
            s.Response(StatusCodes.Status204NoContent, "The choice is recorded");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The caller is not a trainer or nutritionist, the coach role was removed, or a plan choice is required first");
            s.Response(StatusCodes.Status404NotFound, "The caller has no professional profile");
            s.Response(StatusCodes.Status409Conflict, "The coach is on a paid plan or a running given plan (NOT_ON_FREE_PLAN)");
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

        var hasProfile = await db.ProfessionalProfiles.AsNoTracking().AnyAsync(p => p.UserId == userId, ct);

        if (!hasProfile)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var onFreePlan = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Where(CoachSubscriptionStatusClassifier.Matches(AdminCoachStatusFilter.Free, timeProvider.GetUtcNow()))
            .AnyAsync(ct);

        if (!onFreePlan)
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict, ErrorCodes.NotOnFreePlan, "The coach is not on the Free plan.", ct);
            return;
        }

        await coachEvents.RecordAsync(
            CoachEventType.ChosePlan, userId, $"signup-free:{userId}", ct, planCode: SubscriptionPlanCodes.Free);
        await db.SaveChangesAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
