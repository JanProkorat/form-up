/**
 * Fakes for the plan screens of a signed-in coach ('/plan' and Settings > Plan and billing): the plan list, the
 * coach's subscription and roles, and recorders for the billing requests a screen sends. Per-test overrides go on
 * `servePlanScreens`; the billing writes are served by `servePlanPreview`, `serveChangePlan` and `serveCheckout`.
 */
import { afterEach, vi } from 'vitest';
import {
  BillingInterval,
  BillingTaxMode,
  PlanChangeTiming,
  PromotionDuration,
  PromotionKind,
  SecondRoleState,
  SubscriptionPriceLineKind,
  SubscriptionStatus,
} from '@/api/generated';
import type {
  ChangePlanRequest,
  CoachPlanDto,
  CoachPromotionDto,
  CreateCheckoutSessionRequest,
  GetCoachPlansResponse,
  GetMyCoachRolesResponse,
  GetMySubscriptionResponse,
  PreviewPlanChangeRequest,
  PreviewPlanChangeResponse,
} from '@/api/generated';
import { renderWithProviders } from '@/test/render';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { useToastStore } from '@/stores/toast';

export const CHECKOUT_URL = 'https://checkout.stripe.test/c/pay/cs_test_plan_picker';
/** A fixed renewal date, well after the pinned "now" of the tests that read it. */
export const PERIOD_END = '2026-12-19T10:00:00Z';

export function coachPlan(overrides: Partial<CoachPlanDto>): CoachPlanDto {
  return {
    code: 'free',
    nameEn: 'Free',
    nameCs: 'Free',
    nameDe: 'Free',
    priceMinorUnits: 0,
    currency: 'CZK',
    billingInterval: BillingInterval.Monthly,
    maxActiveClients: 2,
    bothRolesIncluded: true,
    canCreatePlans: true,
    canMessage: true,
    ...overrides,
  };
}

/** Free, Start and Pro, both of the paid ones sold monthly and annually; Studio is monthly only. */
export const ANNUAL_PLANS: CoachPlanDto[] = [
  coachPlan({}),
  coachPlan({
    code: 'start',
    nameEn: 'Start',
    priceMinorUnits: 29900,
    secondRoleAddOnPriceMinorUnits: 14900,
    bothRolesIncluded: false,
    annualPriceMinorUnits: 299000,
    annualSecondRoleAddOnPriceMinorUnits: 149000,
    annualSavingPercent: 17,
  }),
  coachPlan({
    code: 'pro',
    nameEn: 'Pro',
    priceMinorUnits: 59900,
    secondRoleAddOnPriceMinorUnits: 29900,
    bothRolesIncluded: false,
    annualPriceMinorUnits: 599000,
    annualSecondRoleAddOnPriceMinorUnits: 299000,
    annualSavingPercent: 17,
  }),
  coachPlan({ code: 'studio', nameEn: 'Studio', priceMinorUnits: 99900, maxActiveClients: undefined }),
];

/** A coach with a live Stripe subscription on `planCode`, billed on `interval`. */
export function liveSubscription(
  planCode: string,
  interval: BillingInterval,
  overrides: Partial<GetMySubscriptionResponse> = {},
): GetMySubscriptionResponse {
  const annual = interval === BillingInterval.Annual;
  const names: Record<string, string> = { start: 'Start', pro: 'Pro' };
  const base = planCode === 'pro' ? 59900 : 29900;
  return {
    planCode,
    nameEn: names[planCode] ?? planCode,
    taxMode: BillingTaxMode.None,
    status: SubscriptionStatus.Active,
    hasBillingAccount: true,
    isSetByFormUp: false,
    isLapsed: false,
    planChoiceRequired: false,
    currentPeriodEndsAt: PERIOD_END,
    secondRole: SecondRoleState.Purchasable,
    activeClientCount: 3,
    clientLimit: 30,
    billingInterval: interval,
    priceLines: [
      {
        kind: SubscriptionPriceLineKind.Plan,
        amountMinorUnits: annual ? base * 10 : base,
        currency: 'CZK',
      },
    ],
    features: { canCreatePlans: true, canMessage: true },
    ...overrides,
  };
}

export const freeSubscription: GetMySubscriptionResponse = {
  planCode: 'free',
  nameEn: 'Free',
  taxMode: BillingTaxMode.None,
  isSetByFormUp: false,
  isLapsed: false,
  planChoiceRequired: false,
  billingInterval: BillingInterval.Monthly,
};

const SINGLE_ROLE: GetMyCoachRolesResponse = {
  roles: [{ role: 'Trainer', clientCount: 0, sharedWithOtherRoleCount: 0 }],
};

interface ServeOptions {
  taxMode?: BillingTaxMode;
  subscription?: GetMySubscriptionResponse;
  plans?: CoachPlanDto[];
  promotion?: CoachPromotionDto;
  roles?: GetMyCoachRolesResponse;
}

/** Serves the reads the plan screens load in the background. */
export function servePlanScreens({
  taxMode = BillingTaxMode.None,
  subscription = freeSubscription,
  plans = ANNUAL_PLANS,
  promotion,
  roles = SINGLE_ROLE,
}: ServeOptions = {}) {
  const body: GetCoachPlansResponse = { plans, taxMode, promotion };
  server.use(
    http.get(apiUrl('/users/me/subscription'), () => HttpResponse.json(subscription)),
    http.get(apiUrl('/users/me/subscription/plans'), () => HttpResponse.json(body)),
    http.get(apiUrl('/users/me/roles'), () => HttpResponse.json(roles)),
  );
}

/** The "now" the date-dependent plan tests run at; only `Date` is faked, so msw and Query timers stay real. */
export const PINNED_NOW = '2026-11-25T12:00:00Z';

/** Pins `Date` to `PINNED_NOW` for one test; the real clock comes back after it. */
export function pinNow() {
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(new Date(PINNED_NOW));
}

afterEach(() => {
  vi.useRealTimers();
});

/** The messages of the toasts shown so far (a page test mounts no Toaster, so the store is the place to read them). */
export const toastMessages = () => useToastStore.getState().toasts.map((toast) => toast.message);

/** The offer the picker tests run under: 30 % off for the first 3 months, claimable until the end of the year. */
export const LAUNCH_OFFER: CoachPromotionDto = {
  name: 'Launch offer',
  kind: PromotionKind.Percent,
  value: 30,
  duration: PromotionDuration.Months,
  durationMonths: 3,
  claimUntil: '2026-12-31T12:00:00Z',
  planCodes: ['start', 'pro', 'studio'],
};

/** The plans as the server shows them under `LAUNCH_OFFER`: it sends the discounted prices, the page never computes them. */
export function plansUnderOffer(coverage: 'all' | 'proOnly'): CoachPlanDto[] {
  const discounted: Record<string, { price: number; addOn?: number }> = {
    start: { price: 20900, addOn: 10500 },
    pro: { price: 41900, addOn: 21000 },
    studio: { price: 69900 },
  };
  return ANNUAL_PLANS.map((plan) => {
    const prices = discounted[plan.code ?? ''];
    const covered = prices !== undefined && (coverage === 'all' || plan.code === 'pro');
    return covered
      ? { ...plan, discountedPriceMinorUnits: prices.price, discountedSecondRoleAddOnPriceMinorUnits: prices.addOn }
      : plan;
  });
}

/** A live paid coach whose switch to `target` is scheduled for the end of the period. */
export function scheduledChangeSubscription(
  target: 'free' | 'start',
  cancellable: boolean,
  overrides: Partial<GetMySubscriptionResponse> = {},
): GetMySubscriptionResponse {
  return liveSubscription('pro', BillingInterval.Monthly, {
    secondRole: SecondRoleState.NotAvailable,
    cancelAtPeriodEnd: target === 'free',
    scheduledPlan: {
      planCode: target,
      nameEn: target === 'free' ? 'Free' : 'Start',
      startsAt: PERIOD_END,
      cancellable,
      billingInterval: BillingInterval.Monthly,
      priceLines: [],
    },
    ...overrides,
  });
}

/** A coach on Pro set by Form Up: no Stripe subscription, no billing account, an optional gift end date. */
export function formUpSubscription(freeUntil?: string): GetMySubscriptionResponse {
  return {
    ...freeSubscription,
    planCode: 'pro',
    nameEn: 'Pro',
    isSetByFormUp: true,
    secondRole: SecondRoleState.NotAvailable,
    freeUntil,
  };
}

/** Records the preview request bodies; answers each with `respond(body)` (a plain "now" preview by default). */
export function servePlanPreview(
  respond: (body: PreviewPlanChangeRequest) => Response = (body) =>
    HttpResponse.json(previewBody({ planCode: body.planCode, billingInterval: body.billingInterval })),
) {
  const bodies: PreviewPlanChangeRequest[] = [];
  server.use(
    http.post(apiUrl('/billing/change-plan/preview'), async ({ request }) => {
      const body = (await request.json()) as PreviewPlanChangeRequest;
      bodies.push(body);
      return respond(body);
    }),
  );
  return bodies;
}

export function previewBody(overrides: Partial<PreviewPlanChangeResponse>): PreviewPlanChangeResponse {
  return {
    planCode: 'pro',
    timing: PlanChangeTiming.Now,
    billingInterval: BillingInterval.Monthly,
    currentBillingInterval: BillingInterval.Monthly,
    currency: 'CZK',
    todayAmountMinorUnits: 15800,
    todayTaxMinorUnits: 0,
    nextAmountMinorUnits: 59900,
    nextChargeAt: PERIOD_END,
    cancelsScheduledSwitchToFree: false,
    taxMode: BillingTaxMode.None,
    ...overrides,
  };
}

/** Records the change-plan request bodies; answers with `respond` (an immediate change unless a test says otherwise). */
export function serveChangePlan(
  respond: () => Response = () => HttpResponse.json({ planCode: 'pro', timing: PlanChangeTiming.Now }),
) {
  const bodies: ChangePlanRequest[] = [];
  server.use(
    http.post(apiUrl('/billing/change-plan'), async ({ request }) => {
      bodies.push((await request.json()) as ChangePlanRequest);
      return respond();
    }),
    // The change refreshes the profile once it lands.
    http.get(apiUrl('/users/me'), () => HttpResponse.json({})),
  );
  return bodies;
}

/** Records the checkout request bodies; answers with `respond` (a Stripe URL unless a test says otherwise). */
export function serveCheckout(respond: () => Response = () => HttpResponse.json({ url: CHECKOUT_URL })) {
  const bodies: CreateCheckoutSessionRequest[] = [];
  server.use(
    http.post(apiUrl('/billing/checkout-session'), async ({ request }) => {
      bodies.push((await request.json()) as CreateCheckoutSessionRequest);
      return respond();
    }),
  );
  return bodies;
}

/** Renders a page element as a signed-in coach of one role (or both). */
export function renderCoachScreen(ui: Parameters<typeof renderWithProviders>[0], path: string, roles = ['Trainer']) {
  return renderWithProviders(ui, { initialEntries: [path], auth: { roles } });
}
