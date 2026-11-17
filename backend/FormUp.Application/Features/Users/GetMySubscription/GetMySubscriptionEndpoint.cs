using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FormUp.Application.Features.Users.GetMySubscription;

/// <summary>
/// Returns the caller's coach subscription block.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="entitlementService">Resolves entitlements, client usage and the second-role state.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="billingOptions">Billing settings, for the tax mode.</param>
/// <param name="coachRoleStatus">Resolves the coach roles that are not removed, for the scheduled plan's add-on line.</param>
internal sealed class GetMySubscriptionEndpoint(
    IApplicationDbContext db,
    EntitlementService entitlementService,
    TimeProvider timeProvider,
    IOptions<BillingOptions> billingOptions,
    ICoachRoleStatus coachRoleStatus)
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
            s.Description = "The caller's plan, status, secondRoleAddOnEndsAt (when a removed second role's add-on stops being billed, " +
                            "or null), active-client usage and limit, second-role state, monthly price lines and the " +
                            "feature flags that currently apply. A coach with no subscription is shown on the Free plan with no status. When the paid " +
                            "plan has lapsed the plan fields read as Free with no status, period end or cancelAtPeriodEnd (all empty once a plan has ended), " +
                            "the lapsed plan's names and end date are returned separately, " +
                            "the active-client count is the kept clients and the over-cap count is reported. When an admin-given plan has ended and no plan " +
                            "was chosen yet, planChoiceRequired is true (never together with isLapsed), the plan fields still describe the ended plan " +
                            "and its names are repeated as the ended-plan names. A coach on an explicit Free plan with clients over the cap gets the " +
                            "kept count and the over-cap count too, as does a coach on a smaller paid plan than their client count. " +
                            "keepClientsChoiceRequired is true while some over-cap client has no kept or released decision yet " +
                            "(never together with planChoiceRequired). scheduledPlan is the plan that starts when the period ends " +
                            "(a scheduled downgrade, or Free when the subscription is set to end) with its names, price lines and start date, or null.";
            s.Response<GetMySubscriptionResponse>(StatusCodes.Status200OK, "The caller's subscription block, with the current promotion and its display-only discount and the plan scheduled to start next while the paid subscription is live");
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

        response.TaxMode = billingOptions.Value.TaxMode;
        response.Promotion = await LoadPromotionAsync(profileId.Value, response, ct);

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

        if (!keepState.IsLapsed)
        {
            response.ScheduledPlan = await LoadScheduledPlanAsync(profileId.Value, userId, response, ct);
        }

        if (keepState.IsPlanChoiceRequired)
        {
            response.PlanChoiceRequired = true;
            response.EndedPlanNameCs = response.NameCs;
            response.EndedPlanNameEn = response.NameEn;
            response.EndedPlanNameDe = response.NameDe;
        }

        response.KeepClientsChoiceRequired = !keepState.IsPlanChoiceRequired && keepState.IsChoicePending;
        response.FreeSlotCount = keepState.IsPlanChoiceRequired ? 0 : keepState.FreeSlotCount;
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
            CanUsePlanTemplates = entitlements.CanUsePlanTemplates,
            CanUseRecipes = entitlements.CanUseRecipes,
            CanUseIngredients = entitlements.CanUseIngredients,
            CanUseProgressPhotos = entitlements.CanUseProgressPhotos,
            CanUseWeightAndMeasurements = entitlements.CanUseWeightAndMeasurements,
            CanUseAutomations = entitlements.CanUseAutomations,
            CanUsePackages = entitlements.CanUsePackages,
        };

        await Send.OkAsync(response, ct);
    }

    /// <summary>
    /// The promotion discounting the coach's live provider subscription, or null, with the display-only discount worked out
    /// from the response's own price lines. A subscription that is not live (Free, canceled, incomplete) shows none.
    /// </summary>
    private async Task<CurrentPromotionDto?> LoadPromotionAsync(long profileId, GetMySubscriptionResponse response, CancellationToken ct)
    {
        if (response.Status is not (SubscriptionStatus.Active or SubscriptionStatus.Trialing or SubscriptionStatus.PastDue))
        {
            return null;
        }

        var current = await db.CoachSubscriptions
            .AsNoTracking()
            .Where(cs => cs.ProfessionalProfileId == profileId && cs.CurrentPromotionId != null)
            .Select(cs => new { Promotion = cs.CurrentPromotion!, cs.CurrentPromotionEndsAt })
            .FirstOrDefaultAsync(ct);

        if (current is null)
        {
            return null;
        }

        var planLine = response.PriceLines.FirstOrDefault(line => line.Kind == SubscriptionPriceLineKind.Plan);
        var addOnLine = response.PriceLines.FirstOrDefault(line => line.Kind == SubscriptionPriceLineKind.SecondRoleAddOn);
        var (discount, discountedTotal) = planLine is null
            ? (0L, 0L)
            : PromotionPricing.ForSubscription(
                current.Promotion, response.PlanCode, planLine.Currency, planLine.AmountMinorUnits, addOnLine?.AmountMinorUnits);

        return new CurrentPromotionDto
        {
            Name = current.Promotion.Name,
            Kind = current.Promotion.Kind,
            Value = current.Promotion.Value,
            Currency = current.Promotion.Currency,
            Duration = current.Promotion.Duration,
            DurationMonths = current.Promotion.DurationMonths,
            EndsAt = current.CurrentPromotionEndsAt,
            DiscountMinorUnits = discount,
            DiscountedTotalMinorUnits = discountedTotal,
        };
    }

    /// <summary>
    /// The plan that starts when the period ends, read from our own row: Free when the subscription is set to end, else the
    /// price a downgrade is scheduled to. Null when nothing is scheduled or the subscription is not live.
    /// </summary>
    private async Task<ScheduledPlanDto?> LoadScheduledPlanAsync(
        long profileId, Guid userId, GetMySubscriptionResponse response, CancellationToken ct)
    {
        if (response.Status is not (SubscriptionStatus.Active or SubscriptionStatus.Trialing or SubscriptionStatus.PastDue))
        {
            return null;
        }

        var row = await db.CoachSubscriptions
            .AsNoTracking()
            .Where(cs => cs.ProfessionalProfileId == profileId)
            .Select(cs => new
            {
                cs.CancelAtPeriodEnd,
                cs.PendingSubscriptionPlanPriceId,
                cs.CurrentPeriodEndsAt,
                cs.GivenFreeAt,
                cs.FreeUntil,
                cs.RenewalCancelledByAdminAt,
                HasProviderSubscription = cs.ExternalSubscriptionId != null,
                IsDisabling = cs.ProfessionalProfile.CoachAccountActiveUntil != null,
            })
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var cancellable = row.HasProviderSubscription
                          && !(row.GivenFreeAt is not null && (row.FreeUntil is null || row.FreeUntil > now))
                          && !row.IsDisabling
                          && row.RenewalCancelledByAdminAt is null;

        if (row.CancelAtPeriodEnd)
        {
            var free = await db.SubscriptionPlans
                .AsNoTracking()
                .Where(p => p.Code == SubscriptionPlanCodes.Free)
                .FirstOrDefaultAsync(ct);

            return free is null
                ? null
                : new ScheduledPlanDto
                {
                    PlanCode = free.Code,
                    NameCs = free.NameCs,
                    NameEn = free.NameEn,
                    NameDe = free.NameDe,
                    StartsAt = row.CurrentPeriodEndsAt,
                    Cancellable = cancellable,
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

        if (row.PendingSubscriptionPlanPriceId is not { } pendingPriceId)
        {
            return null;
        }

        var pending = await db.SubscriptionPlanPrices
            .AsNoTracking()
            .Where(p => p.Id == pendingPriceId)
            .Select(p => new
            {
                p.SubscriptionPlan.Code,
                p.SubscriptionPlan.NameCs,
                p.SubscriptionPlan.NameEn,
                p.SubscriptionPlan.NameDe,
                p.SubscriptionPlan.BothRolesIncluded,
                p.PriceMinorUnits,
                p.SecondRoleAddOnPriceMinorUnits,
                p.Currency,
            })
            .FirstOrDefaultAsync(ct);

        if (pending is null)
        {
            return null;
        }

        var scheduled = new ScheduledPlanDto
        {
            PlanCode = pending.Code,
            NameCs = pending.NameCs,
            NameEn = pending.NameEn,
            NameDe = pending.NameDe,
            StartsAt = row.CurrentPeriodEndsAt,
            Cancellable = cancellable,
            PriceLines =
            [
                new SubscriptionPriceLineDto
                {
                    Kind = SubscriptionPriceLineKind.Plan,
                    AmountMinorUnits = pending.PriceMinorUnits,
                    Currency = pending.Currency,
                },
            ],
        };

        if (!pending.BothRolesIncluded
            && pending.SecondRoleAddOnPriceMinorUnits is > 0 and var addOnPrice
            && (await coachRoleStatus.GetActiveRolesAsync(userId, ct)).Count >= 2)
        {
            scheduled.PriceLines.Add(new SubscriptionPriceLineDto
            {
                Kind = SubscriptionPriceLineKind.SecondRoleAddOn,
                AmountMinorUnits = addOnPrice,
                Currency = pending.Currency,
            });
        }

        return scheduled;
    }

    /// <summary>
    /// A lapsed plan reads as the Free plan: the plan that ended moves to the lapsed fields, the kept clients are the
    /// active clients and the rest are over the cap. The status, period end and cancel flag of the ended subscription are
    /// cleared; the end moment stays in <c>EndedAt</c>. Without a Free plan row the plan fields stay as they were.
    /// </summary>
    private async Task ApplyLapseAsync(GetMySubscriptionResponse response, KeepClientsState keepState, CancellationToken ct)
    {
        response.IsLapsed = true;
        response.LapsedPlanNameCs = response.NameCs;
        response.LapsedPlanNameEn = response.NameEn;
        response.LapsedPlanNameDe = response.NameDe;
        response.EndedAt = response.CurrentPeriodEndsAt;
        response.Status = null;
        response.CurrentPeriodEndsAt = null;
        response.CancelAtPeriodEnd = false;
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
                cs.SecondRoleAddOnEndsAt,
                cs.NextPaymentAttemptAt,
                HasBillingAccount = cs.ExternalCustomerId != null,
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
                NextPaymentAttemptAt = subscription.NextPaymentAttemptAt,
                HasBillingAccount = subscription.HasBillingAccount,
                IsSetByFormUp = subscription.GivenFreeAt is not null
                    && CoachSubscriptionStatusClassifier.PlanApplies(
                        subscription.Status, subscription.FreeUntil, subscription.GivenFreeAt, timeProvider.GetUtcNow()),
                SecondRoleAddOnEndsAt = subscription.Status is SubscriptionStatus.Active or SubscriptionStatus.Trialing or SubscriptionStatus.PastDue
                    && !subscription.CancelAtPeriodEnd
                        ? subscription.SecondRoleAddOnEndsAt
                        : null,
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
