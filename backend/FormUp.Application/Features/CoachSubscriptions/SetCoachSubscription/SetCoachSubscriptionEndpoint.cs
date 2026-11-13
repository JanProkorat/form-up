using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.CoachSubscriptions.SetCoachSubscription;

/// <summary>
/// Assigns a coach's subscription: plan, status, free-features deadline and second-role add-on. Admin-only;
/// billing does not exist yet, so this is how a coach is moved between plans. Ends a given-free plan.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="audit">Audit logging service.</param>
internal sealed class SetCoachSubscriptionEndpoint(IApplicationDbContext db, IAuditService audit)
    : Endpoint<SetCoachSubscriptionRequest, SetCoachSubscriptionResponse>
{
    /// <summary>Audit action of assigning a subscription.</summary>
    public const string AuditAction = "SetCoachSubscription";

    /// <inheritdoc />
    public override void Configure()
    {
        Put("/admin/coaches/{CoachId}/subscription");
        Roles(AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "Assign a coach's subscription";
            s.Description = "Creates the coach's subscription if missing, or replaces its plan, status, free-features deadline and " +
                            "second-role add-on. The coach pays the plan's open price row. An inactive plan may be assigned.";
            s.Response<SetCoachSubscriptionResponse>(StatusCodes.Status200OK, "The subscription as it now stands");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid request body");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "Caller is not an Admin");
            s.Response(StatusCodes.Status404NotFound,
                "The coach (user with a professional profile) or the plan code is unknown (PROFESSIONAL_NOT_FOUND, SUBSCRIPTION_PLAN_NOT_FOUND)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(SetCoachSubscriptionRequest req, CancellationToken ct)
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
        subscription.Status = req.Status;
        subscription.FreeUntil = req.FreeUntil;
        subscription.PaysSecondRoleAddOn = req.PaysSecondRoleAddOn;

        // An explicit assignment ends a given-free plan.
        subscription.GivenFreeAt = null;

        // Back on a paid plan: the kept/released choice made while lapsed no longer applies.
        if (req.PlanCode != SubscriptionPlanCodes.Free
            && req.Status is SubscriptionStatus.Active or SubscriptionStatus.Trialing or SubscriptionStatus.PastDue)
        {
            await CoachLapseLinkReset.ResetAsync(db, profileId.Value, ct);
        }

        await db.SaveChangesAsync(ct);

        var fields = new Dictionary<string, object?>
        {
            ["planCode"] = req.PlanCode,
            ["status"] = req.Status.ToString(),
            ["freeUntil"] = req.FreeUntil?.ToString("O"),
            ["paysSecondRoleAddOn"] = req.PaysSecondRoleAddOn,
        };

        await audit.LogAsync(
            adminId,
            AuditAction,
            nameof(ApplicationUser),
            req.CoachId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            newValues: AuditPayload.WithReason(fields, reason: null),
            ct: ct);

        await Send.OkAsync(new SetCoachSubscriptionResponse
        {
            CoachId = req.CoachId,
            PlanCode = req.PlanCode,
            Status = subscription.Status,
            FreeUntil = subscription.FreeUntil,
            PaysSecondRoleAddOn = subscription.PaysSecondRoleAddOn,
        }, ct);
    }
}
