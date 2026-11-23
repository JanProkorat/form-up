using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.AdminOverview.GetAdminAttention;

/// <summary>
/// Returns the admin overview "needs attention" block. Coach buckets and promotion states come from the shared domain
/// rules, so the counts match the Coaches and Promotions pages.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="timeProvider">Clock.</param>
internal sealed class GetAdminAttentionEndpoint(IApplicationDbContext db, TimeProvider timeProvider)
    : EndpointWithoutRequest<GetAdminAttentionResponse>
{
    private const int HorizonDays = 14;
    private const int ListedCoaches = 3;

    /// <inheritdoc />
    public override void Configure()
    {
        Get("/admin/overview/attention");
        Roles(AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "Get the admin overview attention block";
            s.Description = "Returns the payment-failed count with up to three coaches, the cancelling count with the earliest " +
                            "and latest paid-period end, the count of given-free plans ending within 14 days, and the active " +
                            "promotions that close within 14 days or have at least 90 % of their uses taken.";
            s.Response<GetAdminAttentionResponse>(StatusCodes.Status200OK, "The attention block");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "Caller is not an Admin");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var horizon = now.AddDays(HorizonDays);

        var paymentFailed = db.ProfessionalProfiles
            .AsNoTracking()
            .Where(CoachSubscriptionStatusClassifier.Matches(AdminCoachStatusFilter.PaymentFailed, now));
        var cancelling = db.ProfessionalProfiles
            .AsNoTracking()
            .Where(CoachSubscriptionStatusClassifier.Matches(AdminCoachStatusFilter.Cancelling, now));

        var response = new GetAdminAttentionResponse
        {
            PaymentFailedCount = await paymentFailed.CountAsync(ct),
            PaymentFailedCoaches = await paymentFailed
                .OrderBy(profile => profile.User.LastName)
                .ThenBy(profile => profile.User.FirstName)
                .ThenBy(profile => profile.Id)
                .Take(ListedCoaches)
                .Select(profile => new AdminAttentionCoachDto
                {
                    CoachId = profile.UserId,
                    Name = (profile.User.FirstName + " " + profile.User.LastName).Trim(),
                })
                .ToListAsync(ct),
            CancellingCount = await cancelling.CountAsync(ct),
            CancellingEarliestPeriodEndsAt = await cancelling
                .MinAsync(profile => profile.CoachSubscription!.CurrentPeriodEndsAt, ct),
            CancellingLatestPeriodEndsAt = await cancelling
                .MaxAsync(profile => profile.CoachSubscription!.CurrentPeriodEndsAt, ct),
            GivenFreeEndingSoonCount = await db.ProfessionalProfiles
                .AsNoTracking()
                .Where(CoachSubscriptionStatusClassifier.Matches(AdminCoachStatusFilter.GivenFree, now))
                .CountAsync(
                    profile => profile.CoachSubscription!.FreeUntil != null && profile.CoachSubscription.FreeUntil <= horizon,
                    ct),
            PromotionsEndingSoon = await LoadPromotionsEndingSoonAsync(now, horizon, ct),
        };

        await Send.OkAsync(response, ct);
    }

    private async Task<List<AdminAttentionPromotionDto>> LoadPromotionsEndingSoonAsync(
        DateTimeOffset now, DateTimeOffset horizon, CancellationToken ct)
    {
        var promotions = await db.Promotions
            .AsNoTracking()
            .ToListAsync(ct);
        var usedCounts = await db.PromotionRedemptions
            .AsNoTracking()
            .GroupBy(redemption => redemption.PromotionId)
            .Select(group => new { PromotionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.PromotionId, row => row.Count, ct);

        return promotions
            .Select(promotion => (Promotion: promotion, Used: usedCounts.GetValueOrDefault(promotion.Id)))
            .Where(row => PromotionRules.StatusAt(row.Promotion, row.Used, now) == PromotionStatus.Active)
            .Where(row => row.Promotion.ClaimUntil is { } until && until <= horizon
                          || row.Promotion.MaxUses is { } max && (long)row.Used * 10 >= (long)max * 9)
            .OrderBy(row => row.Promotion.ClaimUntil is null)
            .ThenBy(row => row.Promotion.ClaimUntil)
            .ThenBy(row => row.Promotion.Name)
            .ThenBy(row => row.Promotion.PublicId)
            .Select(row => new AdminAttentionPromotionDto
            {
                PromotionId = row.Promotion.PublicId,
                Name = row.Promotion.Name,
                ClaimUntil = row.Promotion.ClaimUntil,
                UsedCount = row.Used,
                MaxUses = row.Promotion.MaxUses,
            })
            .ToList();
    }
}
