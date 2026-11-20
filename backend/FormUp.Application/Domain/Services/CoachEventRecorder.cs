using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Default <see cref="ICoachEventRecorder"/>. Only adds to the request's context; the caller saves.
/// </summary>
/// <param name="db">Application database context of the request.</param>
/// <param name="revenue">Prices subscriptions for the revenue effects.</param>
/// <param name="timeProvider">Clock.</param>
public sealed class CoachEventRecorder(IApplicationDbContext db, ICoachRevenueService revenue, TimeProvider timeProvider)
    : ICoachEventRecorder
{
    /// <inheritdoc />
    public async Task<bool> RecordAsync(
        CoachEventType type,
        Guid coachUserId,
        string idempotencyKey,
        CancellationToken ct,
        string? planCode = null,
        string? previousPlanCode = null,
        CoachRevenueAmount? before = null,
        CoachRevenueAmount? after = null)
    {
        if (db.CoachEvents.Local.Any(e => e.IdempotencyKey == idempotencyKey)
            || await db.CoachEvents.AnyAsync(e => e.IdempotencyKey == idempotencyKey, ct))
        {
            return false;
        }

        var earlier = before ?? CoachRevenueAmount.None;
        var later = after ?? CoachRevenueAmount.None;
        var currency = later.Currency.Length > 0 ? later.Currency : earlier.Currency;

        db.CoachEvents.Add(new CoachEvent
        {
            CoachUserId = coachUserId,
            EventType = type,
            OccurredAt = timeProvider.GetUtcNow(),
            IdempotencyKey = idempotencyKey,
            PlanCode = planCode,
            PreviousPlanCode = previousPlanCode,
            Currency = currency.Length > 0 ? currency : null,
            FullMonthlyRevenueEffectMinorUnits = later.FullMonthlyMinorUnits - earlier.FullMonthlyMinorUnits,
            DiscountedMonthlyRevenueEffectMinorUnits = later.DiscountedMonthlyMinorUnits - earlier.DiscountedMonthlyMinorUnits,
        });

        return true;
    }

    /// <inheritdoc />
    public async Task RecordSubscriptionChangeAsync(
        string stripeEventId, CoachRevenueState before, CoachSubscription row, CancellationToken ct)
    {
        var after = await CaptureAsync(row, ct);
        CoachEventType? type = null;

        if (!before.IsPaying && after.IsPaying)
        {
            type = CoachEventType.StartedPaidPlan;
        }
        else if (before.IsPaying && after.IsPaying && before.PriceId != after.PriceId)
        {
            // A switch the coach scheduled earlier lands at renewal and is a downgrade by construction.
            if (before.PendingPriceId == after.PriceId || after.ListMonthlyMinorUnits < before.ListMonthlyMinorUnits)
            {
                type = CoachEventType.Downgraded;
            }
            else if (after.ListMonthlyMinorUnits > before.ListMonthlyMinorUnits)
            {
                type = CoachEventType.Upgraded;
            }
        }

        if (type is not { } eventType || await ResolveCoachUserIdAsync(row, ct) is not { } coachUserId)
        {
            return;
        }

        await RecordAsync(
            eventType,
            coachUserId,
            $"{stripeEventId}:{eventType}",
            ct,
            after.PlanCode,
            eventType == CoachEventType.StartedPaidPlan ? null : before.PlanCode,
            before.Amount,
            after.Amount);
    }

    /// <inheritdoc />
    public async Task RecordCancellationAsync(
        string stripeEventId, CoachRevenueState before, CoachSubscription row, CancellationToken ct)
    {
        if (!before.IsPaying || await ResolveCoachUserIdAsync(row, ct) is not { } coachUserId)
        {
            return;
        }

        await RecordAsync(
            CoachEventType.Cancelled,
            coachUserId,
            $"{stripeEventId}:{CoachEventType.Cancelled}",
            ct,
            previousPlanCode: before.PlanCode,
            before: before.Amount);
    }

    /// <inheritdoc />
    public async Task RecordPaymentFailedAsync(string stripeEventId, CoachSubscription row, CancellationToken ct)
    {
        if (await ResolveCoachUserIdAsync(row, ct) is not { } coachUserId)
        {
            return;
        }

        await RecordAsync(
            CoachEventType.PaymentFailed,
            coachUserId,
            $"{stripeEventId}:{CoachEventType.PaymentFailed}",
            ct,
            planCode: (await CaptureAsync(row, ct)).PlanCode);
    }

    /// <inheritdoc />
    public async Task RecordPromotionAppliedAsync(CoachSubscription row, Promotion promotion, CancellationToken ct)
    {
        if (await ResolveCoachUserIdAsync(row, ct) is not { } coachUserId)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var before = await CaptureAsync(row, ct);
        var price = await db.SubscriptionPlanPrices
            .AsNoTracking()
            .Include(p => p.SubscriptionPlan)
            .FirstAsync(p => p.Id == row.SubscriptionPlanPriceId, ct);
        var after = revenue.MonthlyAmount(
            row, now, CoachRevenueLines.Of(price.SubscriptionPlan, price, row.PaysSecondRoleAddOn), promotion, null);

        await RecordAsync(
            CoachEventType.PromotionCodeApplied,
            coachUserId,
            $"promo:{row.ProfessionalProfileId}:{promotion.Id}",
            ct,
            before.PlanCode,
            before.PlanCode,
            before.Amount,
            after);
    }

    /// <inheritdoc />
    public Task<CoachRevenueState> CaptureAsync(CoachSubscription row, CancellationToken ct) =>
        revenue.CaptureStateAsync(row, timeProvider.GetUtcNow(), ct);

    private async Task<Guid?> ResolveCoachUserIdAsync(CoachSubscription row, CancellationToken ct) =>
        await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(profile => profile.Id == row.ProfessionalProfileId)
            .Select(profile => (Guid?)profile.UserId)
            .FirstOrDefaultAsync(ct);
}
