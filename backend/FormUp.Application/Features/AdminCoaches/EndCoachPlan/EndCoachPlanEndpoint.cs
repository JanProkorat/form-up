using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.AdminCoaches.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.AdminCoaches.EndCoachPlan;

/// <summary>
/// Ends a coach's paid or given plan now, so the coach falls back to the Free plan exactly like a lapsed coach.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="timeProvider">Clock.</param>
internal sealed class EndCoachPlanEndpoint(IApplicationDbContext db, IAuditService audit, TimeProvider timeProvider)
    : Endpoint<EndCoachPlanRequest, EndCoachPlanResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/admin/coaches/{CoachId}/end-plan");
        Roles(AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "End a coach's plan now";
            s.Description = "Ends a paying coach's plan (including payment failed and cancelling) or a running gift at once. The coach " +
                            "becomes Free like a lapsed coach: the plan code stays, the status is Canceled, the pending price change, " +
                            "gift dates and cancel-at-period-end flag are cleared, and the kept/released client choice is reset. The " +
                            "reason, ended plan code and whether it was a gift are recorded in the coach's history. There is no payment " +
                            "provider yet: a future Stripe integration must also cancel the subscription in Stripe. An already scheduled " +
                            "account disable is left unchanged.";
            s.Response<EndCoachPlanResponse>(StatusCodes.Status200OK, "The plan that was ended");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid request body");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "Caller is not an Admin");
            s.Response(StatusCodes.Status404NotFound, "The coach is unknown (PROFESSIONAL_NOT_FOUND)");
            s.Response<ProblemDetails>(StatusCodes.Status409Conflict,
                "No plan to end: no subscription, the Free plan, an ended paid plan, or a gift that has expired (NO_PLAN_TO_END)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(EndCoachPlanRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(AppClaims.UserId), out var adminId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var profileId = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(p => p.UserId == req.CoachId)
            .Select(p => (long?)p.Id)
            .FirstOrDefaultAsync(ct);

        if (profileId is null)
        {
            await this.SendProblemAsync(
                StatusCodes.Status404NotFound, ErrorCodes.ProfessionalNotFound, "Coach not found.", ct);
            return;
        }

        var now = timeProvider.GetUtcNow();
        var endable = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(p => p.Id == profileId)
            .AnyAsync(CoachSubscriptionStatusClassifier.Matches(AdminCoachStatusFilter.Paying, now), ct)
            || await db.ProfessionalProfiles
                .AsNoTracking()
                .Where(p => p.Id == profileId)
                .AnyAsync(CoachSubscriptionStatusClassifier.Matches(AdminCoachStatusFilter.GivenFree, now), ct);

        if (!endable)
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict, ErrorCodes.NoPlanToEnd, "The coach has no paid or given plan to end.", ct);
            return;
        }

        var subscription = await db.CoachSubscriptions
            .Include(cs => cs.SubscriptionPlan)
            .FirstAsync(cs => cs.ProfessionalProfileId == profileId, ct);
        var planCode = subscription.SubscriptionPlan.Code;
        var wasGivenFree = subscription.GivenFreeAt is not null;

        // Keeps the paid plan code so the coach reads as lapsed, with the Free cap applied to their clients.
        subscription.Status = SubscriptionStatus.Canceled;
        subscription.PendingSubscriptionPlanPriceId = null;
        subscription.GivenFreeAt = null;
        subscription.FreeUntil = null;
        subscription.CancelAtPeriodEnd = false;

        await CoachLapseLinkReset.ResetAsync(db, profileId.Value, ct);
        await db.SaveChangesAsync(ct);

        var fields = new Dictionary<string, object?>
        {
            ["planCode"] = planCode,
            ["wasGivenFree"] = wasGivenFree,
        };

        await audit.LogAsync(
            adminId,
            AdminCoachAuditActions.EndPlan,
            nameof(ApplicationUser),
            req.CoachId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            newValues: AuditPayload.WithReason(fields, req.Reason.Trim()),
            ct: ct);

        await Send.OkAsync(new EndCoachPlanResponse
        {
            CoachId = req.CoachId,
            PlanCode = planCode,
            WasGivenFree = wasGivenFree,
        }, ct);
    }
}
