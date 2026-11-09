using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Features.SubscriptionPlans.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.SubscriptionPlans.GetSubscriptionPlans;

/// <summary>
/// Lists every subscription tier, including inactive ones, for the Admin management UI.
/// </summary>
/// <param name="db">Application database context.</param>
internal sealed class GetSubscriptionPlansEndpoint(IApplicationDbContext db)
    : EndpointWithoutRequest<GetSubscriptionPlansResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/admin/subscription-plans");
        Roles(AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "List subscription plans";
            s.Description = "Returns every subscription tier, including inactive ones — the Admin management UI needs the full set, not just what's currently offered.";
            s.Response<GetSubscriptionPlansResponse>(StatusCodes.Status200OK, "All subscription plans");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "Caller is not an Admin");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
    {
        var rows = await db.SubscriptionPlans
            .AsNoTracking()
            .OrderBy(p => p.Code)
            .Select(p => new
            {
                Plan = p,
                ExternalPriceId = p.Prices
                    .Where(price => price.ValidTo == null)
                    .Select(price => price.ExternalPriceId)
                    .FirstOrDefault(),
                CoachCount = db.CoachSubscriptions.Count(cs =>
                    cs.SubscriptionPlanId == p.Id && LiveSubscriptionStatuses.All.Contains(cs.Status)),
            })
            .ToListAsync(ct);

        await Send.OkAsync(new GetSubscriptionPlansResponse
        {
            Plans = rows.Select(r => SubscriptionPlanDto.FromEntity(r.Plan, r.ExternalPriceId, r.CoachCount)).ToList(),
        }, ct);
    }
}
