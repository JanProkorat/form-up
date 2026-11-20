using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;

namespace FormUp.Tests.Infrastructure;

/// <summary>
/// Test seam shared by the host: a callback that runs right after the real recorder has added a subscription-change event,
/// so a test can store the same idempotency key through another context, as a concurrent request would. Null by default.
/// </summary>
public sealed class CoachEventRecorderHooks
{
    /// <summary>Called with the Stripe event id and the coach's row after the event is added; reset it in a finally block.</summary>
    public Func<string, CoachSubscription, CancellationToken, Task>? AfterSubscriptionChange { get; set; }
}

/// <summary>
/// The real <see cref="CoachEventRecorder"/> with <see cref="CoachEventRecorderHooks"/> applied. Behaves exactly like the
/// real one while no hook is set.
/// </summary>
public sealed class HookedCoachEventRecorder(ICoachEventRecorder inner, CoachEventRecorderHooks hooks) : ICoachEventRecorder
{
    /// <inheritdoc />
    public Task<bool> RecordAsync(
        CoachEventType type,
        Guid coachUserId,
        string idempotencyKey,
        CancellationToken ct,
        string? planCode = null,
        string? previousPlanCode = null,
        CoachRevenueAmount? before = null,
        CoachRevenueAmount? after = null) =>
        inner.RecordAsync(type, coachUserId, idempotencyKey, ct, planCode, previousPlanCode, before, after);

    /// <inheritdoc />
    public async Task RecordSubscriptionChangeAsync(
        string stripeEventId, CoachRevenueState before, CoachSubscription row, CancellationToken ct)
    {
        await inner.RecordSubscriptionChangeAsync(stripeEventId, before, row, ct);

        if (hooks.AfterSubscriptionChange is { } hook)
        {
            await hook(stripeEventId, row, ct);
        }
    }

    /// <inheritdoc />
    public Task RecordCancellationAsync(string stripeEventId, CoachRevenueState before, CoachSubscription row, CancellationToken ct) =>
        inner.RecordCancellationAsync(stripeEventId, before, row, ct);

    /// <inheritdoc />
    public Task RecordPaymentFailedAsync(string stripeEventId, CoachSubscription row, CancellationToken ct) =>
        inner.RecordPaymentFailedAsync(stripeEventId, row, ct);

    /// <inheritdoc />
    public Task RecordPromotionAppliedAsync(CoachSubscription row, Promotion promotion, CancellationToken ct) =>
        inner.RecordPromotionAppliedAsync(row, promotion, ct);

    /// <inheritdoc />
    public Task<CoachRevenueState> CaptureAsync(CoachSubscription row, CancellationToken ct) => inner.CaptureAsync(row, ct);
}
