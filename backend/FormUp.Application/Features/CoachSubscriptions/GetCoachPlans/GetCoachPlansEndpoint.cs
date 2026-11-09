using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.CoachSubscriptions.GetCoachPlans;

/// <summary>
/// Lists the active subscription plans with their current prices, for a coach choosing a plan. Coach-only: the
/// client app never sees a price.
/// </summary>
/// <param name="db">Application database context.</param>
internal sealed class GetCoachPlansEndpoint(IApplicationDbContext db) : EndpointWithoutRequest<GetCoachPlansResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/users/me/subscription/plans");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "List the plans a coach can choose";
            s.Description = "Active plans with names in all locales, current price, second-role add-on price, currency, " +
                            "client cap, feature flags and whether both roles are included.";
            s.Response<GetCoachPlansResponse>(StatusCodes.Status200OK, "The active plans, cheapest first");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The caller is not a trainer or nutritionist");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
    {
        var plans = await db.SubscriptionPlans
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.PriceMinorUnits)
            .ThenBy(p => p.Code)
            .Select(p => new CoachPlanDto
            {
                Code = p.Code,
                NameCs = p.NameCs,
                NameEn = p.NameEn,
                NameDe = p.NameDe,
                PriceMinorUnits = p.PriceMinorUnits,
                SecondRoleAddOnPriceMinorUnits = p.SecondRoleAddOnPriceMinorUnits,
                Currency = p.Currency,
                BillingInterval = p.BillingInterval,
                MaxActiveClients = p.MaxActiveClients,
                BothRolesIncluded = p.BothRolesIncluded,
                CanCreatePlans = p.CanCreatePlans,
                CanMessage = p.CanMessage,
                CanSendQuestionnaires = p.CanSendQuestionnaires,
                CanUseWeeklyCheckIns = p.CanUseWeeklyCheckIns,
                CanUsePerClientCheckInConfig = p.CanUsePerClientCheckInConfig,
                CanUsePayments = p.CanUsePayments,
            })
            .ToListAsync(ct);

        await Send.OkAsync(new GetCoachPlansResponse { Plans = plans }, ct);
    }
}
