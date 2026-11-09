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

namespace FormUp.Application.Features.AdminCoaches.GiveCoachPlanFree;

/// <summary>
/// Gives a coach a paid plan for free, optionally until a date. Shows as "Given free" and, once the gift ends,
/// the coach falls back to the Free plan like a lapsed coach.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="timeProvider">Clock.</param>
internal sealed class GiveCoachPlanFreeEndpoint(IApplicationDbContext db, IAuditService audit, TimeProvider timeProvider)
    : Endpoint<GiveCoachPlanFreeRequest, GiveCoachPlanFreeResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/admin/coaches/{CoachId}/give-free");
        Roles(AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "Give a coach a plan for free";
            s.Description = "Puts the coach on the plan's open price row as a gift: Active, no pending price change, and the kept/released " +
                            "client choice made while lapsed is reset. The gift ends at FreeUntil, or never when it is null. The reason is " +
                            "recorded in the coach's history. Replaces any earlier subscription assignment.";
            s.Response<GiveCoachPlanFreeResponse>(StatusCodes.Status200OK, "The gift as it now stands");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid request body");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "Caller is not an Admin");
            s.Response(StatusCodes.Status404NotFound,
                "The coach or the plan code is unknown (PROFESSIONAL_NOT_FOUND, SUBSCRIPTION_PLAN_NOT_FOUND)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GiveCoachPlanFreeRequest req, CancellationToken ct)
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

        var openPrice = await db.SubscriptionPlanPrices
            .AsNoTracking()
            .Where(p => p.SubscriptionPlan.Code == req.PlanCode && p.ValidTo == null)
            .Select(p => new { p.Id, p.SubscriptionPlanId })
            .FirstOrDefaultAsync(ct);

        if (openPrice is null)
        {
            await this.SendProblemAsync(
                StatusCodes.Status404NotFound, ErrorCodes.SubscriptionPlanNotFound, "Subscription plan not found.", ct);
            return;
        }

        var now = timeProvider.GetUtcNow();
        var subscription = await db.CoachSubscriptions
            .FirstOrDefaultAsync(cs => cs.ProfessionalProfileId == profileId, ct);

        if (subscription is null)
        {
            subscription = new CoachSubscription
            {
                ProfessionalProfileId = profileId.Value,
                SubscriptionPlanId = openPrice.SubscriptionPlanId,
                SubscriptionPlanPriceId = openPrice.Id,
            };
            db.CoachSubscriptions.Add(subscription);
        }

        subscription.SubscriptionPlanId = openPrice.SubscriptionPlanId;
        subscription.SubscriptionPlanPriceId = openPrice.Id;
        subscription.PendingSubscriptionPlanPriceId = null;
        subscription.GivenFreeAt = now;
        subscription.FreeUntil = req.FreeUntil;
        subscription.Status = SubscriptionStatus.Active;

        await CoachLapseLinkReset.ResetAsync(db, profileId.Value, ct);
        await db.SaveChangesAsync(ct);

        var fields = new Dictionary<string, object?>
        {
            ["planCode"] = req.PlanCode,
            ["freeUntil"] = req.FreeUntil?.ToString("O"),
        };

        await audit.LogAsync(
            adminId,
            AdminCoachAuditActions.GivePlanFree,
            nameof(ApplicationUser),
            req.CoachId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            newValues: AuditPayload.WithReason(fields, req.Reason.Trim()),
            ct: ct);

        await Send.OkAsync(new GiveCoachPlanFreeResponse
        {
            CoachId = req.CoachId,
            PlanCode = req.PlanCode,
            GivenFreeAt = now,
            FreeUntil = req.FreeUntil,
        }, ct);
    }
}
