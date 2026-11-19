using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Services.Billing;

namespace FormUp.Tests.Infrastructure;

/// <summary>
/// In-memory <see cref="ISubscriptionService"/> for tests: records every call, makes no network call, and can be told to
/// be unconfigured or to fail. The factory registers one shared instance, so a test that uses it calls <see cref="Reset"/> first.
/// </summary>
public sealed class FakeSubscriptionService : ISubscriptionService
{
    private readonly object gate = new();
    private int counter;

    /// <summary>Whether the fake reports itself as configured.</summary>
    public bool IsConfigured { get; set; } = true;

    /// <summary>When true every call throws <see cref="BillingProviderException"/>.</summary>
    public bool Fail { get; set; }

    /// <summary>When true only <see cref="ISubscriptionService.ChangePlanAsync"/> throws <see cref="BillingProviderException"/>.</summary>
    public bool FailPlanChanges { get; set; }

    /// <summary>When true only <see cref="ISubscriptionService.CreatePriceAsync"/> for the annual interval throws <see cref="BillingProviderException"/>.</summary>
    public bool FailAnnualPriceCreation { get; set; }

    /// <summary>Recorded <see cref="ISubscriptionService.CreateCustomerAsync"/> calls.</summary>
    public List<CustomerCall> Customers { get; } = [];

    /// <summary>Recorded <see cref="ISubscriptionService.CreateCheckoutSessionAsync"/> calls.</summary>
    public List<CheckoutCall> Checkouts { get; } = [];

    /// <summary>Recorded <see cref="ISubscriptionService.CreatePortalSessionAsync"/> calls (customer ids).</summary>
    public List<string> Portals { get; } = [];

    /// <summary>Recorded <see cref="ISubscriptionService.ChangePlanAsync"/> calls.</summary>
    public List<ChangePlanCall> PlanChanges { get; } = [];

    /// <summary>What <see cref="ISubscriptionService.UpgradePlanNowAsync"/> returns; each call is also recorded in <see cref="PlanChanges"/> as an immediate change.</summary>
    public PlanUpgradeOutcome UpgradeOutcome { get; set; } = PlanUpgradeOutcome.Applied;

    /// <summary>Recorded <see cref="ISubscriptionService.RestoreAfterExpiredUpgradeAsync"/> calls.</summary>
    public List<RestoreCall> ExpiredUpgradeRestores { get; } = [];

    /// <summary>When true only <see cref="ISubscriptionService.RestoreAfterExpiredUpgradeAsync"/> throws <see cref="BillingProviderException"/>.</summary>
    public bool FailExpiredUpgradeRestore { get; set; }

    /// <summary>Recorded <see cref="ISubscriptionService.ClearScheduledPriceChangeAsync"/> calls (subscription ids).</summary>
    public List<string> ClearedScheduledChanges { get; } = [];

    /// <summary>When true only <see cref="ISubscriptionService.ClearScheduledPriceChangeAsync"/> throws <see cref="BillingProviderException"/>.</summary>
    public bool FailClearScheduledChange { get; set; }

    /// <summary>Recorded <see cref="ISubscriptionService.CreatePriceAsync"/> calls.</summary>
    public List<PriceCall> Prices { get; } = [];

    /// <summary>Recorded <see cref="ISubscriptionService.ApplyPromotionCodeAsync"/> calls (subscription ids only; never the code text).</summary>
    public List<string> PromotionCodeSubscriptions { get; } = [];

    /// <summary>Recorded provider promotion code ids passed to <see cref="ISubscriptionService.ApplyPromotionCodeAsync"/>.</summary>
    public List<string> AppliedPromotionCodeIds { get; } = [];

    /// <summary>Recorded <see cref="ISubscriptionService.CreateCouponAsync"/> calls.</summary>
    public List<CouponCall> Coupons { get; } = [];

    /// <summary>Recorded <see cref="ISubscriptionService.CreatePromotionCodeAsync"/> calls.</summary>
    public List<PromotionCodeCall> PromotionCodes { get; } = [];

    /// <summary>Recorded <see cref="ISubscriptionService.DeleteCouponAsync"/> calls (coupon ids).</summary>
    public List<string> DeletedCoupons { get; } = [];

    /// <summary>Recorded <see cref="ISubscriptionService.DeactivatePromotionCodeAsync"/> calls (provider promotion code ids).</summary>
    public List<string> DeactivatedPromotionCodes { get; } = [];

    /// <summary>When true only <see cref="ISubscriptionService.CreatePromotionCodeAsync"/> throws <see cref="BillingProviderException"/>.</summary>
    public bool FailPromotionCodeCreation { get; set; }

    /// <summary>What <see cref="ISubscriptionService.GetSubscriptionDiscountAsync"/> returns, by provider subscription id.</summary>
    public Dictionary<string, SubscriptionDiscount> SubscriptionDiscounts { get; } = [];

    /// <summary>Recorded <see cref="ISubscriptionService.CancelAtPeriodEndAsync"/> calls (subscription ids).</summary>
    public List<string> CancelledAtPeriodEnd { get; } = [];

    /// <summary>Recorded <see cref="ISubscriptionService.CancelNowAsync"/> calls (subscription ids).</summary>
    public List<string> CancelledNow { get; } = [];

    /// <summary>What <see cref="ISubscriptionService.CancelNowAsync"/> returns.</summary>
    public CancelRefundResult CancelNowResult { get; set; } = CancelRefundResult.None;

    /// <summary>Runs inside <see cref="ISubscriptionService.CancelNowAsync"/> before it returns, to stage what Stripe's webhook does meanwhile. Cleared by <see cref="Reset"/>.</summary>
    public Func<string, Task>? CancelNowHook { get; set; }

    /// <summary>Recorded <see cref="ISubscriptionService.ResumeRenewalAsync"/> calls (subscription ids).</summary>
    public List<string> ResumedRenewals { get; } = [];

    /// <summary>When true only <see cref="ISubscriptionService.ResumeRenewalAsync"/> throws <see cref="BillingProviderException"/>.</summary>
    public bool FailResumeRenewal { get; set; }

    /// <summary>What <see cref="ISubscriptionService.PreviewPlanChangeAsync"/> returns (its currency is replaced by the call's).</summary>
    public PlanChangePreview PlanChangePreviewResult { get; set; } = new(24500, 0, "CZK");

    /// <summary>Recorded <see cref="ISubscriptionService.PreviewPlanChangeAsync"/> calls.</summary>
    public List<PlanChangePreviewCall> PlanChangePreviews { get; } = [];

    /// <summary>What <see cref="ISubscriptionService.PreviewSecondRoleAddOnAsync"/> returns.</summary>
    public SecondRoleAddOnPreview PreviewResult { get; set; } = new(12300, 0, "CZK");

    /// <summary>Recorded <see cref="ISubscriptionService.PreviewSecondRoleAddOnAsync"/> calls.</summary>
    public List<AddOnCall> Previews { get; } = [];

    /// <summary>What <see cref="ISubscriptionService.ChargeSecondRoleAddOnAsync"/> returns.</summary>
    public AddOnChargeResult ChargeResult { get; set; } = AddOnChargeResult.Charged;

    /// <summary>Recorded <see cref="ISubscriptionService.ChargeSecondRoleAddOnAsync"/> calls.</summary>
    public List<AddOnCall> Charges { get; } = [];

    /// <summary>A recorded customer creation.</summary>
    public sealed record CustomerCall(Guid UserId, string Email, string? Name, string? Language);

    /// <summary>A recorded checkout session creation.</summary>
    public sealed record CheckoutCall(
        string CustomerId,
        Guid UserId,
        string PlanCode,
        string PlanPriceExternalId,
        long? AddOnAmountMinorUnits,
        string? CouponId,
        string? Language,
        CheckoutFlow Flow = CheckoutFlow.Settings,
        BillingInterval Interval = BillingInterval.Monthly);

    /// <summary>A recorded plan change.</summary>
    public sealed record ChangePlanCall(
        string SubscriptionId,
        string PlanCode,
        string PlanPriceExternalId,
        bool AtNextRenewal,
        long? AddOnAmountMinorUnits = null,
        string? IdempotencyKey = null,
        BillingInterval Interval = BillingInterval.Monthly);

    /// <summary>A recorded restore after an expired pending upgrade.</summary>
    public sealed record RestoreCall(string SubscriptionId, bool KeepScheduledChange);

    /// <summary>A recorded plan-change preview.</summary>
    public sealed record PlanChangePreviewCall(
        string CustomerId,
        string SubscriptionId,
        string PlanCode,
        string PlanPriceExternalId,
        long? AddOnAmountMinorUnits,
        BillingInterval Interval = BillingInterval.Monthly);

    /// <summary>A recorded add-on preview or charge; the idempotency key is null for a preview.</summary>
    public sealed record AddOnCall(
        string SubscriptionId,
        string PlanCode,
        long AmountMinorUnits,
        string? IdempotencyKey,
        string? RoleToAdd = null,
        BillingInterval Interval = BillingInterval.Monthly);

    /// <summary>Recorded <see cref="ISubscriptionService.ClearPendingSecondRoleAsync"/> calls (subscription ids).</summary>
    public List<string> ClearedPendingSecondRoles { get; } = [];

    /// <summary>A recorded price creation.</summary>
    public sealed record PriceCall(string PlanCode, long AmountMinorUnits, string Currency, BillingInterval Interval);

    /// <summary>A recorded coupon creation and the id the fake returned.</summary>
    public sealed record CouponCall(CouponSpec Spec, string CouponId);

    /// <summary>A recorded promotion code creation and the id the fake returned. Holds the code text a test chose.</summary>
    public sealed record PromotionCodeCall(string CouponId, string Code, DateTimeOffset? ExpiresAt, bool FirstTimeOnly, string PromotionCodeId);

    /// <summary>Clears every recorded call and restores the defaults.</summary>
    public void Reset()
    {
        lock (gate)
        {
            IsConfigured = true;
            Fail = false;
            FailAnnualPriceCreation = false;
            FailPlanChanges = false;
            FailClearScheduledChange = false;
            UpgradeOutcome = PlanUpgradeOutcome.Applied;
            ExpiredUpgradeRestores.Clear();
            ClearedPendingSecondRoles.Clear();
            FailExpiredUpgradeRestore = false;
            ClearedScheduledChanges.Clear();
            Customers.Clear();
            Checkouts.Clear();
            Portals.Clear();
            PlanChanges.Clear();
            Prices.Clear();
            PromotionCodeSubscriptions.Clear();
            AppliedPromotionCodeIds.Clear();
            Coupons.Clear();
            PromotionCodes.Clear();
            DeletedCoupons.Clear();
            DeactivatedPromotionCodes.Clear();
            FailPromotionCodeCreation = false;
            SubscriptionDiscounts.Clear();
            CancelledAtPeriodEnd.Clear();
            CancelledNow.Clear();
            CancelNowResult = CancelRefundResult.None;
            CancelNowHook = null;
            ResumedRenewals.Clear();
            FailResumeRenewal = false;
            PreviewResult = new SecondRoleAddOnPreview(12300, 0, "CZK");
            PlanChangePreviewResult = new PlanChangePreview(24500, 0, "CZK");
            PlanChangePreviews.Clear();
            Previews.Clear();
            ChargeResult = AddOnChargeResult.Charged;
            Charges.Clear();
        }
    }

    /// <inheritdoc />
    public Task<string> CreateCustomerAsync(Guid userId, string email, string? name, string? language, CancellationToken ct)
    {
        ThrowIfFailing();

        lock (gate)
        {
            Customers.Add(new CustomerCall(userId, email, name, language));

            return Task.FromResult($"cus_fake_{++counter}");
        }
    }

    /// <inheritdoc />
    public Task<string> CreateCheckoutSessionAsync(
        string customerId,
        Guid userId,
        string planCode,
        string planPriceExternalId,
        long? addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        string? couponId,
        CheckoutFlow flow,
        string? language,
        CancellationToken ct)
    {
        ThrowIfFailing();

        lock (gate)
        {
            Checkouts.Add(new CheckoutCall(customerId, userId, planCode, planPriceExternalId, addOnAmountMinorUnits, couponId, language, flow, interval));
        }

        return Task.FromResult("https://checkout.stripe.test/session");
    }

    /// <inheritdoc />
    public Task<string> CreatePortalSessionAsync(string customerId, string? language, CancellationToken ct)
    {
        ThrowIfFailing();

        lock (gate)
        {
            Portals.Add(customerId);
        }

        return Task.FromResult("https://portal.stripe.test/session");
    }

    /// <inheritdoc />
    public Task ChangePlanAsync(
        string subscriptionId,
        string planCode,
        string planPriceExternalId,
        long? addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        bool atNextRenewal,
        CancellationToken ct,
        string? idempotencyKey = null)
    {
        ThrowIfFailing();

        if (FailPlanChanges)
        {
            throw new BillingProviderException("Fake plan change failure.");
        }

        lock (gate)
        {
            PlanChanges.Add(new ChangePlanCall(subscriptionId, planCode, planPriceExternalId, atNextRenewal, addOnAmountMinorUnits, idempotencyKey, interval));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PlanUpgradeOutcome> UpgradePlanNowAsync(
        string subscriptionId,
        string planCode,
        string planPriceExternalId,
        long? addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        string idempotencyKey,
        CancellationToken ct)
    {
        ThrowIfFailing();

        if (FailPlanChanges)
        {
            throw new BillingProviderException("Fake plan change failure.");
        }

        lock (gate)
        {
            PlanChanges.Add(new ChangePlanCall(subscriptionId, planCode, planPriceExternalId, false, addOnAmountMinorUnits, idempotencyKey, interval));
        }

        return Task.FromResult(UpgradeOutcome);
    }

    /// <inheritdoc />
    public Task RestoreAfterExpiredUpgradeAsync(string subscriptionId, bool keepScheduledChange, CancellationToken ct)
    {
        ThrowIfFailing();

        if (FailExpiredUpgradeRestore)
        {
            throw new BillingProviderException("Fake expired upgrade restore failure.");
        }

        lock (gate)
        {
            ExpiredUpgradeRestores.Add(new RestoreCall(subscriptionId, keepScheduledChange));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearScheduledPriceChangeAsync(string subscriptionId, CancellationToken ct)
    {
        ThrowIfFailing();

        if (FailClearScheduledChange)
        {
            throw new BillingProviderException("Fake clear scheduled change failure.");
        }

        lock (gate)
        {
            ClearedScheduledChanges.Add(subscriptionId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PlanChangePreview> PreviewPlanChangeAsync(
        string customerId,
        string subscriptionId,
        string planCode,
        string planPriceExternalId,
        long? addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        CancellationToken ct)
    {
        ThrowIfFailing();

        lock (gate)
        {
            PlanChangePreviews.Add(new PlanChangePreviewCall(customerId, subscriptionId, planCode, planPriceExternalId, addOnAmountMinorUnits, interval));
        }

        return Task.FromResult(PlanChangePreviewResult with { Currency = currency });
    }

    /// <inheritdoc />
    public Task ApplyPromotionCodeAsync(string subscriptionId, string promotionCodeId, CancellationToken ct)
    {
        ThrowIfFailing();

        lock (gate)
        {
            PromotionCodeSubscriptions.Add(subscriptionId);
            AppliedPromotionCodeIds.Add(promotionCodeId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string> CreateCouponAsync(CouponSpec spec, CancellationToken ct)
    {
        ThrowIfFailing();

        lock (gate)
        {
            var couponId = $"coupon_fake_{++counter}";
            Coupons.Add(new CouponCall(spec, couponId));

            return Task.FromResult(couponId);
        }
    }

    /// <inheritdoc />
    public Task<string> CreatePromotionCodeAsync(
        string couponId, string code, DateTimeOffset? expiresAt, bool firstTimeOnly, CancellationToken ct)
    {
        ThrowIfFailing();

        if (FailPromotionCodeCreation)
        {
            throw new BillingProviderException("Fake promotion code failure.");
        }

        lock (gate)
        {
            var promotionCodeId = $"promo_fake_{++counter}";
            PromotionCodes.Add(new PromotionCodeCall(couponId, code, expiresAt, firstTimeOnly, promotionCodeId));

            return Task.FromResult(promotionCodeId);
        }
    }

    /// <inheritdoc />
    public Task DeactivatePromotionCodeAsync(string promotionCodeId, CancellationToken ct)
    {
        ThrowIfFailing();

        lock (gate)
        {
            DeactivatedPromotionCodes.Add(promotionCodeId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteCouponAsync(string couponId, CancellationToken ct)
    {
        ThrowIfFailing();

        lock (gate)
        {
            DeletedCoupons.Add(couponId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SubscriptionDiscount?> GetSubscriptionDiscountAsync(string subscriptionId, CancellationToken ct)
    {
        ThrowIfFailing();

        lock (gate)
        {
            return Task.FromResult(SubscriptionDiscounts.GetValueOrDefault(subscriptionId));
        }
    }

    /// <inheritdoc />
    public Task<string> CreatePriceAsync(
        string planCode, long amountMinorUnits, string currency, BillingInterval interval, CancellationToken ct)
    {
        ThrowIfFailing();

        if (FailAnnualPriceCreation && interval == BillingInterval.Annual)
        {
            throw new BillingProviderException("Annual price creation failed.");
        }

        lock (gate)
        {
            Prices.Add(new PriceCall(planCode, amountMinorUnits, currency, interval));

            return Task.FromResult($"price_fake_{++counter}");
        }
    }

    /// <inheritdoc />
    public Task CancelAtPeriodEndAsync(string subscriptionId, CancellationToken ct)
    {
        ThrowIfFailing();

        lock (gate)
        {
            CancelledAtPeriodEnd.Add(subscriptionId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<CancelRefundResult> CancelNowAsync(string subscriptionId, CancellationToken ct)
    {
        ThrowIfFailing();

        CancelRefundResult result;

        lock (gate)
        {
            CancelledNow.Add(subscriptionId);
            result = CancelNowResult;
        }

        if (CancelNowHook is { } hook)
        {
            await hook(subscriptionId);
        }

        return result;
    }

    /// <inheritdoc />
    public Task ResumeRenewalAsync(string subscriptionId, CancellationToken ct)
    {
        ThrowIfFailing();

        if (FailResumeRenewal)
        {
            throw new BillingProviderException("Fake resume renewal failure.");
        }

        lock (gate)
        {
            ResumedRenewals.Add(subscriptionId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SecondRoleAddOnPreview> PreviewSecondRoleAddOnAsync(
        string customerId,
        string subscriptionId,
        string planCode,
        long addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        CancellationToken ct)
    {
        ThrowIfFailing();

        lock (gate)
        {
            Previews.Add(new AddOnCall(subscriptionId, planCode, addOnAmountMinorUnits, null, Interval: interval));
        }

        return Task.FromResult(PreviewResult with { Currency = currency });
    }

    /// <inheritdoc />
    public Task<AddOnChargeResult> ChargeSecondRoleAddOnAsync(
        string subscriptionId,
        string planCode,
        long addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        string idempotencyKey,
        CancellationToken ct,
        string? roleToAdd = null)
    {
        ThrowIfFailing();

        lock (gate)
        {
            Charges.Add(new AddOnCall(subscriptionId, planCode, addOnAmountMinorUnits, idempotencyKey, roleToAdd, interval));
        }

        return Task.FromResult(ChargeResult);
    }

    /// <inheritdoc />
    public Task ClearPendingSecondRoleAsync(string subscriptionId, CancellationToken ct)
    {
        ThrowIfFailing();

        lock (gate)
        {
            ClearedPendingSecondRoles.Add(subscriptionId);
        }

        return Task.CompletedTask;
    }

    private void ThrowIfFailing()
    {
        if (Fail)
        {
            throw new BillingProviderException("Fake provider failure.");
        }
    }
}
