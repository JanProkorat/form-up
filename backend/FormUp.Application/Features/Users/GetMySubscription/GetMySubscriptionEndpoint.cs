using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Users.GetMySubscription;

/// <summary>
/// Returns the caller's coach subscription block.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="entitlementService">Resolves entitlements, client usage and the second-role state.</param>
/// <param name="timeProvider">Clock.</param>
internal sealed class GetMySubscriptionEndpoint(
    IApplicationDbContext db, EntitlementService entitlementService, TimeProvider timeProvider)
    : EndpointWithoutRequest<GetMySubscriptionResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/users/me/subscription");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Get my coach subscription";
            s.Description = "The caller's plan, status, active-client usage and limit, second-role state, monthly price lines and the " +
                            "feature flags that currently apply. A coach with no subscription is shown on the Free plan with no status. When the paid " +
                            "plan has lapsed the plan fields read as Free, the lapsed plan's names and end date are returned separately, " +
                            "the active-client count is the kept clients and the over-cap count is reported. When an admin-given plan has ended and no plan " +
                            "was chosen yet, planChoiceRequired is true (never together with isLapsed), the plan fields still describe the ended plan " +
                            "and its names are repeated as the ended-plan names. A coach on an explicit Free plan with clients over the cap gets the " +
                            "kept count and the over-cap count too, as does a coach on a smaller paid plan than their client count. " +
                            "keepClientsChoiceRequired is true while some over-cap client has no kept or released decision yet " +
                            "(never together with planChoiceRequired).";
            s.Response<GetMySubscriptionResponse>(StatusCodes.Status200OK, "The caller's subscription block");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The caller is not a trainer or nutritionist");
            s.Response(StatusCodes.Status404NotFound, "The caller has no professional profile, or the Free plan is missing");
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

        var profileId = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => (long?)p.Id)
            .FirstOrDefaultAsync(ct);

        if (profileId is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var response = await LoadPlanBlockAsync(profileId.Value, ct);

        if (response is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var entitlements = await entitlementService.GetEntitlementsAsync(profileId.Value, ct);

        var keepState = await entitlementService.GetKeepClientsStateAsync(profileId.Value, ct);

        if (keepState.IsLapsed)
        {
            await ApplyLapseAsync(response, keepState, ct);
        }
        else if (keepState.OverCapClientProfileIds.Count > 0)
        {
            response.ActiveClientCount = keepState.KeptClientProfileIds.Count;
            response.OverCapClientCount = keepState.OverCapClientProfileIds.Count;
        }
        else
        {
            response.ActiveClientCount = await entitlementService.GetActiveClientCountAsync(profileId.Value, ct);
        }

        if (keepState.IsPlanChoiceRequired)
        {
            response.PlanChoiceRequired = true;
            response.EndedPlanNameCs = response.NameCs;
            response.EndedPlanNameEn = response.NameEn;
            response.EndedPlanNameDe = response.NameDe;
        }

        response.KeepClientsChoiceRequired = !keepState.IsPlanChoiceRequired && keepState.IsChoicePending;
        response.ClientLimit = entitlements.MaxActiveClients;
        response.SecondRole = await entitlementService.GetSecondRoleStateAsync(profileId.Value, ct);
        response.Features = new SubscriptionFeaturesDto
        {
            CanCreatePlans = entitlements.CanCreatePlans,
            CanMessage = entitlements.CanMessage,
            CanSendQuestionnaires = entitlements.CanSendQuestionnaires,
            CanUseWeeklyCheckIns = entitlements.CanUseWeeklyCheckIns,
            CanUsePerClientCheckInConfig = entitlements.CanUsePerClientCheckInConfig,
            CanUsePayments = entitlements.CanUsePayments,
        };

        await Send.OkAsync(response, ct);
    }

    /// <summary>
    /// A lapsed plan reads as the Free plan: the plan that ended moves to the lapsed fields, the kept clients are the
    /// active clients and the rest are over the cap. Without a Free plan row the plan fields stay as they were.
    /// </summary>
    private async Task ApplyLapseAsync(GetMySubscriptionResponse response, KeepClientsState keepState, CancellationToken ct)
    {
        response.IsLapsed = true;
        response.LapsedPlanNameCs = response.NameCs;
        response.LapsedPlanNameEn = response.NameEn;
        response.LapsedPlanNameDe = response.NameDe;
        response.EndedAt = response.CurrentPeriodEndsAt;
        response.ActiveClientCount = keepState.KeptClientProfileIds.Count;
        response.OverCapClientCount = keepState.OverCapClientProfileIds.Count;

        var free = await db.SubscriptionPlans
            .AsNoTracking()
            .Where(p => p.Code == SubscriptionPlanCodes.Free)
            .FirstOrDefaultAsync(ct);

        if (free is null)
        {
            return;
        }

        response.PlanCode = free.Code;
        response.NameCs = free.NameCs;
        response.NameEn = free.NameEn;
        response.NameDe = free.NameDe;
        response.PriceLines =
        [
            new SubscriptionPriceLineDto
            {
                Kind = SubscriptionPriceLineKind.Plan,
                AmountMinorUnits = free.PriceMinorUnits,
                Currency = free.Currency,
            },
        ];
    }

    /// <summary>
    /// Plan identity, status and price lines — from the subscription row, or from the Free plan when there is none.
    /// </summary>
    private async Task<GetMySubscriptionResponse?> LoadPlanBlockAsync(long profileId, CancellationToken ct)
    {
        var subscription = await db.CoachSubscriptions
            .AsNoTracking()
            .Where(cs => cs.ProfessionalProfileId == profileId)
            .Select(cs => new
            {
                cs.SubscriptionPlan.Code,
                cs.SubscriptionPlan.NameCs,
                cs.SubscriptionPlan.NameEn,
                cs.SubscriptionPlan.NameDe,
                cs.Status,
                cs.CurrentPeriodEndsAt,
                cs.CancelAtPeriodEnd,
                cs.FreeUntil,
                cs.GivenFreeAt,
                cs.PaysSecondRoleAddOn,
                cs.SubscriptionPlanPrice.PriceMinorUnits,
                cs.SubscriptionPlanPrice.SecondRoleAddOnPriceMinorUnits,
                cs.SubscriptionPlanPrice.Currency,
            })
            .FirstOrDefaultAsync(ct);

        if (subscription is not null)
        {
            var response = new GetMySubscriptionResponse
            {
                PlanCode = subscription.Code,
                NameCs = subscription.NameCs,
                NameEn = subscription.NameEn,
                NameDe = subscription.NameDe,
                Status = subscription.Status,
                CurrentPeriodEndsAt = subscription.CurrentPeriodEndsAt,
                CancelAtPeriodEnd = subscription.CancelAtPeriodEnd,
                FreeUntil = subscription.FreeUntil,
                IsSetByFormUp = subscription.GivenFreeAt is not null
                    && CoachSubscriptionStatusClassifier.PlanApplies(
                        subscription.Status, subscription.FreeUntil, subscription.GivenFreeAt, timeProvider.GetUtcNow()),
                PriceLines =
                [
                    new SubscriptionPriceLineDto
                    {
                        Kind = SubscriptionPriceLineKind.Plan,
                        AmountMinorUnits = subscription.PriceMinorUnits,
                        Currency = subscription.Currency,
                    },
                ],
            };

            if (subscription.PaysSecondRoleAddOn && subscription.SecondRoleAddOnPriceMinorUnits is { } addOnPrice)
            {
                response.PriceLines.Add(new SubscriptionPriceLineDto
                {
                    Kind = SubscriptionPriceLineKind.SecondRoleAddOn,
                    AmountMinorUnits = addOnPrice,
                    Currency = subscription.Currency,
                });
            }

            return response;
        }

        var free = await db.SubscriptionPlans
            .AsNoTracking()
            .Where(p => p.Code == SubscriptionPlanCodes.Free)
            .FirstOrDefaultAsync(ct);

        if (free is null)
        {
            return null;
        }

        return new GetMySubscriptionResponse
        {
            PlanCode = free.Code,
            NameCs = free.NameCs,
            NameEn = free.NameEn,
            NameDe = free.NameDe,
            PriceLines =
            [
                new SubscriptionPriceLineDto
                {
                    Kind = SubscriptionPriceLineKind.Plan,
                    AmountMinorUnits = free.PriceMinorUnits,
                    Currency = free.Currency,
                },
            ],
        };
    }
}
