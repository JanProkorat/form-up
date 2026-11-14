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

    /// <summary>Recorded <see cref="ISubscriptionService.CreateCustomerAsync"/> calls.</summary>
    public List<CustomerCall> Customers { get; } = [];

    /// <summary>Recorded <see cref="ISubscriptionService.CreateCheckoutSessionAsync"/> calls.</summary>
    public List<CheckoutCall> Checkouts { get; } = [];

    /// <summary>Recorded <see cref="ISubscriptionService.CreatePortalSessionAsync"/> calls (customer ids).</summary>
    public List<string> Portals { get; } = [];

    /// <summary>Recorded <see cref="ISubscriptionService.ChangePlanAsync"/> calls.</summary>
    public List<ChangePlanCall> PlanChanges { get; } = [];

    /// <summary>Recorded <see cref="ISubscriptionService.CreatePriceAsync"/> calls.</summary>
    public List<PriceCall> Prices { get; } = [];

    /// <summary>Recorded <see cref="ISubscriptionService.ApplyGivenFreeAsync"/> calls.</summary>
    public List<GivenFreeCall> GivenFree { get; } = [];

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

    /// <summary>When true only <see cref="ISubscriptionService.CreatePromotionCodeAsync"/> throws <see cref="BillingProviderException"/>.</summary>
    public bool FailPromotionCodeCreation { get; set; }

    /// <summary>What <see cref="ISubscriptionService.GetSubscriptionDiscountAsync"/> returns, by provider subscription id.</summary>
    public Dictionary<string, SubscriptionDiscount> SubscriptionDiscounts { get; } = [];

    /// <summary>Recorded <see cref="ISubscriptionService.CancelAtPeriodEndAsync"/> calls (subscription ids).</summary>
    public List<string> CancelledAtPeriodEnd { get; } = [];

    /// <summary>What <see cref="ISubscriptionService.PreviewSecondRoleAddOnAsync"/> returns.</summary>
    public SecondRoleAddOnPreview PreviewResult { get; set; } = new(12300, 0, "CZK");

    /// <summary>Recorded <see cref="ISubscriptionService.PreviewSecondRoleAddOnAsync"/> calls.</summary>
    public List<AddOnCall> Previews { get; } = [];

    /// <summary>What <see cref="ISubscriptionService.ChargeSecondRoleAddOnAsync"/> returns.</summary>
    public AddOnChargeOutcome ChargeOutcome { get; set; } = AddOnChargeOutcome.Charged;

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
        CheckoutFlow Flow = CheckoutFlow.Settings);

    /// <summary>A recorded plan change.</summary>
    public sealed record ChangePlanCall(
        string SubscriptionId, string PlanCode, string PlanPriceExternalId, bool AtNextRenewal, long? AddOnAmountMinorUnits = null);

    /// <summary>A recorded add-on preview or charge; the idempotency key is null for a preview.</summary>
    public sealed record AddOnCall(string SubscriptionId, string PlanCode, long AmountMinorUnits, string? IdempotencyKey);

    /// <summary>A recorded price creation.</summary>
    public sealed record PriceCall(string PlanCode, long AmountMinorUnits, string Currency, BillingInterval Interval);

    /// <summary>A recorded given-free call.</summary>
    public sealed record GivenFreeCall(string SubscriptionId, DateTimeOffset? FreeUntil);

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
            FailPlanChanges = false;
            Customers.Clear();
            Checkouts.Clear();
            Portals.Clear();
            PlanChanges.Clear();
            Prices.Clear();
            GivenFree.Clear();
            PromotionCodeSubscriptions.Clear();
            AppliedPromotionCodeIds.Clear();
            Coupons.Clear();
            PromotionCodes.Clear();
            DeletedCoupons.Clear();
            FailPromotionCodeCreation = false;
            SubscriptionDiscounts.Clear();
            CancelledAtPeriodEnd.Clear();
            PreviewResult = new SecondRoleAddOnPreview(12300, 0, "CZK");
            Previews.Clear();
            ChargeOutcome = AddOnChargeOutcome.Charged;
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
            Checkouts.Add(new CheckoutCall(customerId, userId, planCode, planPriceExternalId, addOnAmountMinorUnits, couponId, language, flow));
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
        CancellationToken ct)
    {
        ThrowIfFailing();

        if (FailPlanChanges)
        {
            throw new BillingProviderException("Fake plan change failure.");
        }

        lock (gate)
        {
            PlanChanges.Add(new ChangePlanCall(subscriptionId, planCode, planPriceExternalId, atNextRenewal, addOnAmountMinorUnits));
        }

        return Task.CompletedTask;
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

        lock (gate)
        {
            Prices.Add(new PriceCall(planCode, amountMinorUnits, currency, interval));

            return Task.FromResult($"price_fake_{++counter}");
        }
    }

    /// <inheritdoc />
    public Task ApplyGivenFreeAsync(string subscriptionId, DateTimeOffset? freeUntil, CancellationToken ct)
    {
        ThrowIfFailing();

        lock (gate)
        {
            GivenFree.Add(new GivenFreeCall(subscriptionId, freeUntil));
        }

        return Task.CompletedTask;
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
            Previews.Add(new AddOnCall(subscriptionId, planCode, addOnAmountMinorUnits, null));
        }

        return Task.FromResult(PreviewResult with { Currency = currency });
    }

    /// <inheritdoc />
    public Task<AddOnChargeOutcome> ChargeSecondRoleAddOnAsync(
        string subscriptionId,
        string planCode,
        long addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        string idempotencyKey,
        CancellationToken ct)
    {
        ThrowIfFailing();

        lock (gate)
        {
            Charges.Add(new AddOnCall(subscriptionId, planCode, addOnAmountMinorUnits, idempotencyKey));
        }

        return Task.FromResult(ChargeOutcome);
    }

    private void ThrowIfFailing()
    {
        if (Fail)
        {
            throw new BillingProviderException("Fake provider failure.");
        }
    }
}
