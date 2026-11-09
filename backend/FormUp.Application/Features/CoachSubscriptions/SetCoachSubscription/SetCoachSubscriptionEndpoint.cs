using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.CoachSubscriptions.SetCoachSubscription;

/// <summary>
/// Assigns a coach's subscription: plan, status, free-features deadline and second-role add-on. Admin-only;
/// billing does not exist yet, so this is how a coach is moved between plans.
/// </summary>
/// <param name="db">Application database context.</param>
internal sealed class SetCoachSubscriptionEndpoint(IApplicationDbContext db)
    : Endpoint<SetCoachSubscriptionRequest, SetCoachSubscriptionResponse>
{
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
            s.Responses[StatusCodes.Status200OK] = "The subscription as it now stands";
            s.Responses[StatusCodes.Status400BadRequest] = "Invalid request body";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or invalid credentials";
            s.Responses[StatusCodes.Status403Forbidden] = "Caller is not an Admin";
            s.Responses[StatusCodes.Status404NotFound] =
                "The coach (user with a professional profile) or the plan code is unknown (PROFESSIONAL_NOT_FOUND, SUBSCRIPTION_PLAN_NOT_FOUND)";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(SetCoachSubscriptionRequest req, CancellationToken ct)
    {
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

        // Back on a paid plan: the kept/released choice made while lapsed no longer applies.
        if (req.PlanCode != SubscriptionPlanCodes.Free
            && req.Status is SubscriptionStatus.Active or SubscriptionStatus.Trialing or SubscriptionStatus.PastDue)
        {
            var lapseLinks = await db.ClientProfessionalLinks
                .Where(link => link.ProfessionalProfileId == profileId
                               && (link.ConnectionState == ConnectionState.FreePlanKept
                                   || link.ConnectionState == ConnectionState.FreePlanReleased))
                .ToListAsync(ct);

            foreach (var link in lapseLinks)
            {
                link.ConnectionState = ConnectionState.Connected;
            }
        }

        await db.SaveChangesAsync(ct);

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
