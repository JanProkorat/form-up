using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.SubscriptionPlans.DeactivateSubscriptionPlan;

/// <summary>
/// Soft-deactivates a subscription tier by setting <c>IsActive = false</c>. Never deletes the
/// row — <c>CoachSubscription</c> FK history must survive. Idempotent: deactivating an
/// already-inactive plan still returns 204.
/// </summary>
/// <param name="db">Application database context.</param>
internal sealed class DeactivateSubscriptionPlanEndpoint(IApplicationDbContext db)
    : Endpoint<DeactivateSubscriptionPlanRequest, object>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete("/admin/subscription-plans/{Code}");
        Roles(AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "Deactivate subscription plan";
            s.Description = "Soft-deactivates a subscription tier (IsActive = false). The row is never deleted — CoachSubscription FK history must survive. Idempotent.";
            s.Response(StatusCodes.Status204NoContent, "Plan deactivated (or was already inactive)");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid plan code");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "Caller is not an Admin");
            s.Response(StatusCodes.Status404NotFound, "No plan with this Code");
            s.Response(StatusCodes.Status409Conflict, "The Free plan cannot be deactivated");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(DeactivateSubscriptionPlanRequest req, CancellationToken ct)
    {
        var plan = await db.SubscriptionPlans.FirstOrDefaultAsync(p => p.Code == req.Code, ct);

        if (plan is null)
        {
            await this.SendProblemAsync(
                StatusCodes.Status404NotFound,
                ErrorCodes.SubscriptionPlanNotFound,
                "No subscription plan with this Code.",
                ct);
            return;
        }

        if (plan.Code == SubscriptionPlanCodes.Free)
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict,
                ErrorCodes.FreePlanLocked,
                "The Free plan must stay at price 0 and cannot be deactivated.",
                ct);
            return;
        }

        if (plan.IsActive)
        {
            plan.IsActive = false;
            await db.SaveChangesAsync(ct);
        }

        await Send.NoContentAsync(ct);
    }
}
