import {
  BillingTaxMode,
  SubscriptionStatus,
  type CoachPlanDto,
  type GetMySubscriptionResponse,
} from '@/api/generated';

/** Code of the plan every coach starts on. */
export const FREE_PLAN_CODE = 'free';

/** VAT rate in percent per tax mode; the mode comes from the server, never from the client. */
const TAX_RATE_PERCENT: Record<BillingTaxMode, number> = {
  [BillingTaxMode.None]: 0,
  [BillingTaxMode.Vat21]: 21,
};

/** The VAT rate to name in the "prices without VAT" line, or null when no VAT line is shown. */
export function vatRatePercent(taxMode: BillingTaxMode | undefined): number | null {
  const rate = taxMode ? TAX_RATE_PERCENT[taxMode] : 0;
  return rate > 0 ? rate : null;
}

/** True when the plan charges a separate add-on for the second role. */
export function secondRoleAddOnOf(plan: CoachPlanDto): number | null {
  if (plan.bothRolesIncluded) return null;
  return plan.secondRoleAddOnPriceMinorUnits ?? null;
}

/** What the plan costs per interval for a coach with one role, or with both roles when `withSecondRole`. */
export function planDisplayPrice(plan: CoachPlanDto, withSecondRole: boolean): number {
  const base = plan.priceMinorUnits ?? 0;
  const addOn = withSecondRole ? secondRoleAddOnOf(plan) : null;
  return base + (addOn ?? 0);
}

/**
 * What the plan costs per interval after the automatic promotion, in the same shape as {@link planDisplayPrice},
 * or null when no promotion covers the plan. Display only: both parts come from the server.
 */
export function planDiscountedPrice(plan: CoachPlanDto, withSecondRole: boolean): number | null {
  const base = plan.discountedPriceMinorUnits;
  if (base === undefined || base === null) return null;
  const addOn = withSecondRole ? (plan.discountedSecondRoleAddOnPriceMinorUnits ?? 0) : 0;
  return base + addOn;
}

/**
 * True while the coach pays for a plan through a live Stripe subscription they can change in place:
 * a paid plan that is not given by Form Up and is neither lapsed nor never completed.
 */
export function hasLivePaidSubscription(data: GetMySubscriptionResponse | undefined): boolean {
  if (!data || data.isSetByFormUp || data.isLapsed || data.planChoiceRequired) return false;
  if (!data.planCode || data.planCode === FREE_PLAN_CODE) return false;
  return (
    data.status === SubscriptionStatus.Active ||
    data.status === SubscriptionStatus.Trialing ||
    data.status === SubscriptionStatus.PastDue
  );
}

/** True once a checkout has landed: a paid plan that Stripe, not Form Up, keeps active. */
export function isCheckoutSettled(data: GetMySubscriptionResponse | undefined): boolean {
  return (
    !!data &&
    !data.isSetByFormUp &&
    !!data.planCode &&
    data.planCode !== FREE_PLAN_CODE &&
    (data.status === SubscriptionStatus.Active || data.status === SubscriptionStatus.Trialing)
  );
}
