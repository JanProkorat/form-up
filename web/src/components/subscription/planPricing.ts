import {
  BillingInterval,
  BillingTaxMode,
  SecondRoleState,
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

/** The price fields of a plan, shared by the coach and the public plan payloads. */
export type PlanPriceFields = Pick<
  CoachPlanDto,
  'priceMinorUnits' | 'bothRolesIncluded' | 'secondRoleAddOnPriceMinorUnits'
>;

/** The promotion fields of a plan, shared by the coach and the public plan payloads. */
export type PlanDiscountFields = Pick<
  CoachPlanDto,
  'discountedPriceMinorUnits' | 'discountedSecondRoleAddOnPriceMinorUnits'
>;

/** True when the plan charges a separate add-on for the second role. */
export function secondRoleAddOnOf(plan: PlanPriceFields): number | null {
  if (plan.bothRolesIncluded) return null;
  return plan.secondRoleAddOnPriceMinorUnits ?? null;
}

/** What the plan costs per interval for a coach with one role, or with both roles when `withSecondRole`. */
export function planDisplayPrice(plan: PlanPriceFields, withSecondRole: boolean): number {
  const base = plan.priceMinorUnits ?? 0;
  const addOn = withSecondRole ? secondRoleAddOnOf(plan) : null;
  return base + (addOn ?? 0);
}

/**
 * What the plan costs per interval after the automatic promotion, in the same shape as {@link planDisplayPrice},
 * or null when no promotion covers the plan. Display only: both parts come from the server.
 */
export function planDiscountedPrice(plan: PlanDiscountFields, withSecondRole: boolean): number | null {
  const base = plan.discountedPriceMinorUnits;
  if (base === undefined || base === null) return null;
  const addOn = withSecondRole ? (plan.discountedSecondRoleAddOnPriceMinorUnits ?? 0) : 0;
  return base + addOn;
}

/** The annual price fields of a plan, shared by the coach and the public plan payloads. */
export type PlanAnnualFields = Pick<
  CoachPlanDto,
  | 'annualPriceMinorUnits'
  | 'annualSecondRoleAddOnPriceMinorUnits'
  | 'annualDiscountedPriceMinorUnits'
  | 'annualDiscountedSecondRoleAddOnPriceMinorUnits'
  | 'annualSavingPercent'
>;

/** Everything needed to price a plan on either billing interval. */
export type PlanIntervalFields = PlanPriceFields & PlanDiscountFields & PlanAnnualFields;

/** True when the plan has an open annual price. */
export function isSoldAnnually(plan: PlanAnnualFields): boolean {
  return plan.annualPriceMinorUnits !== undefined && plan.annualPriceMinorUnits !== null;
}

/** True when any plan is sold annually: only then the Monthly | Annual switch is shown. */
export function anyPlanSoldAnnually(plans: PlanAnnualFields[]): boolean {
  return plans.some(isSoldAnnually);
}

/** The largest whole-percent annual saving across the plans, or null when no plan saves anything. */
export function maxAnnualSavingPercent(plans: PlanAnnualFields[]): number | null {
  const best = Math.max(0, ...plans.map((plan) => plan.annualSavingPercent ?? 0));
  return best > 0 ? best : null;
}

/**
 * The interval a plan is actually shown and bought on when the coach picked `chosen`: the Free plan follows the
 * choice (it shows "0 / year"), a paid plan without an open annual price stays monthly.
 */
export function effectiveInterval(
  plan: PlanAnnualFields & Pick<CoachPlanDto, 'priceMinorUnits'>,
  chosen: BillingInterval,
): BillingInterval {
  if (chosen !== BillingInterval.Annual) return BillingInterval.Monthly;
  const free = (plan.priceMinorUnits ?? 0) === 0;
  return free || isSoldAnnually(plan) ? BillingInterval.Annual : BillingInterval.Monthly;
}

/** The second-role add-on per interval, or null when none is charged. */
export function secondRoleAddOnFor(plan: PlanIntervalFields, interval: BillingInterval): number | null {
  if (interval !== BillingInterval.Annual) return secondRoleAddOnOf(plan);
  if (plan.bothRolesIncluded) return null;
  return plan.annualSecondRoleAddOnPriceMinorUnits ?? null;
}

/** What the plan costs per `interval`, with the second-role add-on when `withSecondRole`. Free costs 0 on both. */
export function planPriceFor(plan: PlanIntervalFields, interval: BillingInterval, withSecondRole: boolean): number {
  if (interval !== BillingInterval.Annual) return planDisplayPrice(plan, withSecondRole);
  const addOn = withSecondRole ? secondRoleAddOnFor(plan, interval) : null;
  return (plan.annualPriceMinorUnits ?? 0) + (addOn ?? 0);
}

/** The price per `interval` after the automatic promotion, or null when no promotion covers it. Display only. */
export function planDiscountedPriceFor(
  plan: PlanIntervalFields,
  interval: BillingInterval,
  withSecondRole: boolean,
): number | null {
  if (interval !== BillingInterval.Annual) return planDiscountedPrice(plan, withSecondRole);
  const base = plan.annualDiscountedPriceMinorUnits;
  if (base === undefined || base === null) return null;
  return base + (withSecondRole ? (plan.annualDiscountedSecondRoleAddOnPriceMinorUnits ?? 0) : 0);
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

/** True once an in-place upgrade has landed: the coach is on the target plan, kept active by Stripe. */
export function isPlanChangeSettled(data: GetMySubscriptionResponse | undefined, target: string): boolean {
  return (
    !!data &&
    !data.isSetByFormUp &&
    data.planCode === target &&
    (data.status === SubscriptionStatus.Active || data.status === SubscriptionStatus.Trialing)
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

/** True once the second-role add-on is paid; the coach then adds the role from Settings at no further charge. */
export function isSecondRoleAddOnSettled(data: GetMySubscriptionResponse | undefined): boolean {
  return !!data && data.secondRole === SecondRoleState.PaidAddOn;
}
