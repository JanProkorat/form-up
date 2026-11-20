/**
 * '/register/plan' — the sign-up plan step: the Monthly | Annual switch and yearly prices, and the "Have a code?"
 * field that rides along with the checkout request. The subscription, plan and role reads are served here.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import {
  BillingInterval,
  BillingTaxMode,
  PromotionBillingPeriod,
  PromotionDuration,
  PromotionKind,
} from '@/api/generated';
import type {
  CoachPlanDto,
  CoachPromotionDto,
  CreateCheckoutSessionRequest,
  GetCoachPlansResponse,
  GetMyCoachRolesResponse,
  GetMySubscriptionResponse,
} from '@/api/generated';
import RegisterPlanPage from '@/pages/RegisterPlanPage';
import { renderWithProviders } from '@/test/render';
import { HttpResponse, apiUrl, http, problem } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { useToastStore } from '@/stores/toast';

const CHECKOUT_URL = 'https://checkout.stripe.test/c/pay/cs_test_register';

function plan(overrides: Partial<CoachPlanDto>): CoachPlanDto {
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

const ANNUAL_PLANS: CoachPlanDto[] = [
  plan({}),
  plan({
    code: 'start',
    nameEn: 'Start',
    priceMinorUnits: 29900,
    secondRoleAddOnPriceMinorUnits: 14900,
    bothRolesIncluded: false,
    annualPriceMinorUnits: 299000,
    annualSecondRoleAddOnPriceMinorUnits: 149000,
    annualSavingPercent: 17,
  }),
  plan({
    code: 'pro',
    nameEn: 'Pro',
    priceMinorUnits: 59900,
    secondRoleAddOnPriceMinorUnits: 29900,
    bothRolesIncluded: false,
    annualPriceMinorUnits: 599000,
    annualSecondRoleAddOnPriceMinorUnits: 299000,
    annualSavingPercent: 17,
  }),
];

const MONTHLY_ONLY_PLANS: CoachPlanDto[] = ANNUAL_PLANS.map((entry) => ({
  ...entry,
  annualPriceMinorUnits: undefined,
  annualSecondRoleAddOnPriceMinorUnits: undefined,
  annualSavingPercent: undefined,
}));

const subscription: GetMySubscriptionResponse = {
  planCode: 'free',
  nameEn: 'Free',
  taxMode: BillingTaxMode.None,
  isSetByFormUp: false,
  isLapsed: false,
  planChoiceRequired: false,
};

const roles: GetMyCoachRolesResponse = {
  roles: [{ role: 'Trainer', clientCount: 0, sharedWithOtherRoleCount: 0 }],
};

interface ServeOptions {
  plans?: CoachPlanDto[];
  promotion?: CoachPromotionDto;
}

function serve({ plans = ANNUAL_PLANS, promotion }: ServeOptions = {}) {
  const body: GetCoachPlansResponse = { plans, taxMode: BillingTaxMode.None, promotion };
  server.use(
    http.get(apiUrl('/users/me/subscription'), () => HttpResponse.json(subscription)),
    http.get(apiUrl('/users/me/subscription/plans'), () => HttpResponse.json(body)),
    http.get(apiUrl('/users/me/roles'), () => HttpResponse.json(roles)),
  );
}

/** Records the checkout request bodies; answers with `respond` (a Stripe URL unless a test says otherwise). */
function serveCheckout(respond: () => Response = () => HttpResponse.json({ url: CHECKOUT_URL })) {
  const bodies: CreateCheckoutSessionRequest[] = [];
  server.use(
    http.post(apiUrl('/billing/checkout-session'), async ({ request }) => {
      bodies.push((await request.json()) as CreateCheckoutSessionRequest);
      return respond();
    }),
  );
  return bodies;
}

function renderPlanStep() {
  return renderWithProviders(<RegisterPlanPage />, {
    initialEntries: ['/register/plan'],
    auth: { roles: ['Trainer'] },
  });
}

async function selectPlan(user: ReturnType<typeof renderPlanStep>['user'], name: string) {
  const card = await screen.findByRole('article', { name });
  await user.click(within(card).getByRole('button', { name: `Select ${name}` }));
}

const bar = () => screen.getByRole('region', { name: 'Selected plan' });

afterEach(() => {
  vi.unstubAllGlobals();
  useToastStore.setState({ toasts: [] });
});

describe('RegisterPlanPage billing interval', () => {
  it('shows the switch on Monthly with the saving badge and monthly prices', async () => {
    serve();

    renderPlanStep();

    const group = await screen.findByRole('group', { name: 'Billing period' });
    expect(within(group).getByRole('button', { name: 'Monthly' })).toHaveAttribute('aria-pressed', 'true');
    expect(within(group).getByRole('button', { name: /Annual/ })).toHaveAttribute('aria-pressed', 'false');
    expect(within(group).getByText('Save up to 17 %')).toBeInTheDocument();
    const pro = screen.getByRole('article', { name: 'Pro' });
    expect(within(pro).getByText(/599/)).toBeInTheDocument();
    expect(within(pro).getByText('/ month')).toBeInTheDocument();
    expect(screen.queryByText(/Paid once a year/)).not.toBeInTheDocument();
  });

  it('Annual shows the yearly prices, "/ year", the footnote and Free at 0 per year', async () => {
    serve();

    const { user } = renderPlanStep();
    await user.click(await screen.findByRole('button', { name: /Annual/ }));

    const pro = screen.getByRole('article', { name: 'Pro' });
    expect(within(pro).getByText(/5,990/)).toBeInTheDocument();
    expect(within(pro).getByText('/ year')).toBeInTheDocument();
    expect(within(pro).getByText(/Second role later \+.*2,990/)).toBeInTheDocument();
    const free = screen.getByRole('article', { name: 'Free' });
    expect(within(free).getByText(/^.*0$/, { selector: 'span.text-stat' })).toBeInTheDocument();
    expect(within(free).getByText('/ year')).toBeInTheDocument();
    expect(screen.getByText('Paid once a year. You can switch to monthly when the year ends.')).toBeInTheDocument();
  });

  it('hides the switch and the badge when no plan is sold annually', async () => {
    serve({ plans: MONTHLY_ONLY_PLANS });

    renderPlanStep();

    expect(await screen.findByRole('article', { name: 'Pro' })).toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'Billing period' })).not.toBeInTheDocument();
  });

  it('shows no saving badge when no plan saves anything', async () => {
    serve({ plans: ANNUAL_PLANS.map((entry) => ({ ...entry, annualSavingPercent: undefined })) });

    renderPlanStep();

    expect(await screen.findByRole('group', { name: 'Billing period' })).toBeInTheDocument();
    expect(screen.queryByText(/Save up to/)).not.toBeInTheDocument();
  });

  it('a paid plan without an annual price keeps its monthly price on Annual', async () => {
    serve({ plans: [...ANNUAL_PLANS.slice(0, 2), { ...ANNUAL_PLANS[2], annualPriceMinorUnits: undefined }] });
    const bodies = serveCheckout();

    const { user } = renderPlanStep();
    await user.click(await screen.findByRole('button', { name: /Annual/ }));

    const pro = screen.getByRole('article', { name: 'Pro' });
    expect(within(pro).getByText('/ month')).toBeInTheDocument();

    vi.stubGlobal('location', { ...window.location, assign: vi.fn() });
    await selectPlan(user, 'Pro');
    await user.click(within(bar()).getByRole('button', { name: 'Continue to payment' }));

    await vi.waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0].billingInterval).toBe(BillingInterval.Monthly);
  });

  it('on Annual the promotion shows without a struck price, and the banner names the first year', async () => {
    const promotion: CoachPromotionDto = {
      name: 'Launch offer',
      kind: PromotionKind.Percent,
      value: 30,
      duration: PromotionDuration.Months,
      durationMonths: 3,
      billingPeriod: PromotionBillingPeriod.Both,
      planCodes: ['start', 'pro'],
    };
    serve({
      promotion,
      plans: ANNUAL_PLANS.map((entry) =>
        entry.code === 'pro'
          ? { ...entry, discountedPriceMinorUnits: 41900, annualDiscountedPriceMinorUnits: 419300 }
          : entry,
      ),
    });

    const { user } = renderPlanStep();
    expect(await screen.findByRole('status')).toHaveTextContent('Launch offer: −30 % for your first 3 months');
    expect(screen.getByRole('status')).not.toHaveTextContent('first year on annual');

    await user.click(screen.getByRole('button', { name: /Annual/ }));

    expect(screen.getByRole('status')).toHaveTextContent('or your first year on annual');
    const pro = screen.getByRole('article', { name: 'Pro' });
    expect(within(pro).getByText(/4,193/)).toBeInTheDocument();
    expect(within(pro).queryByText(/Regular price/)).not.toBeInTheDocument();
    expect(within(pro).getByText(/−30 % on the first year, then .*5,990/)).toBeInTheDocument();
  });

  it('a monthly-only promotion is described as such on Annual and takes nothing off the yearly prices', async () => {
    serve({
      promotion: {
        name: 'Launch offer',
        kind: PromotionKind.Percent,
        value: 30,
        duration: PromotionDuration.Months,
        durationMonths: 3,
        billingPeriod: PromotionBillingPeriod.Monthly,
        planCodes: ['start', 'pro'],
      },
    });

    const { user } = renderPlanStep();
    await user.click(await screen.findByRole('button', { name: /Annual/ }));

    expect(screen.getByRole('status')).toHaveTextContent('on monthly billing only');
    expect(screen.getByRole('status')).toHaveTextContent("It doesn't apply to this billing period.");
    expect(within(screen.getByRole('article', { name: 'Pro' })).queryByText(/then/)).not.toBeInTheDocument();
  });
});

describe('RegisterPlanPage checkout', () => {
  it('sends the chosen interval and no code by default', async () => {
    serve();
    const bodies = serveCheckout();
    vi.stubGlobal('location', { ...window.location, assign: vi.fn() });

    const { user } = renderPlanStep();
    await user.click(await screen.findByRole('button', { name: /Annual/ }));
    await selectPlan(user, 'Pro');
    await user.click(within(bar()).getByRole('button', { name: 'Continue to payment' }));

    await vi.waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]).toEqual({
      planCode: 'pro',
      billingInterval: BillingInterval.Annual,
      includeSecondRoleAddOn: false,
      flow: 'SignUp',
    });
  });

  it('Apply keeps the trimmed code in the browser and Continue sends it with the interval', async () => {
    serve();
    const bodies = serveCheckout();
    const assign = vi.fn();
    vi.stubGlobal('location', { ...window.location, assign });

    const { user, router } = renderPlanStep();
    await user.click(await screen.findByRole('button', { name: /Annual/ }));
    await selectPlan(user, 'Pro');
    await user.type(within(bar()).getByLabelText('Have a code?'), '  YEAR40 ');
    await user.click(within(bar()).getByRole('button', { name: 'Apply' }));

    // No request yet, no discount text, and the code stays out of the URL.
    expect(within(bar()).getByText('Code YEAR40')).toBeInTheDocument();
    expect(within(bar()).getByRole('button', { name: 'Remove code' })).toBeInTheDocument();
    expect(bodies).toHaveLength(0);
    expect(router.state.location.search).toBe('');

    await user.click(within(bar()).getByRole('button', { name: 'Continue to payment' }));

    await vi.waitFor(() => expect(assign).toHaveBeenCalledWith(CHECKOUT_URL));
    expect(bodies).toEqual([
      { planCode: 'pro', billingInterval: BillingInterval.Annual, includeSecondRoleAddOn: false, flow: 'SignUp', code: 'YEAR40' },
    ]);
  });

  it('Remove code sends the next checkout without it', async () => {
    serve();
    const bodies = serveCheckout();
    vi.stubGlobal('location', { ...window.location, assign: vi.fn() });

    const { user } = renderPlanStep();
    await selectPlan(user, 'Pro');
    await user.type(within(bar()).getByLabelText('Have a code?'), 'YEAR40');
    await user.click(within(bar()).getByRole('button', { name: 'Apply' }));
    await user.click(within(bar()).getByRole('button', { name: 'Remove code' }));
    await user.click(within(bar()).getByRole('button', { name: 'Continue to payment' }));

    await vi.waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]).not.toHaveProperty('code');
  });

  it('BILLING_ALREADY_SUBSCRIBED falls back to a plan change with the same interval and no code', async () => {
    serve();
    serveCheckout(() => problem(409, 'BILLING_ALREADY_SUBSCRIBED'));
    const changes: Array<{ planCode: string; billingInterval?: BillingInterval; code?: string }> = [];
    server.use(
      http.post(apiUrl('/billing/change-plan'), async ({ request }) => {
        changes.push((await request.json()) as (typeof changes)[number]);
        return HttpResponse.json({ planCode: 'pro', timing: 'Now' });
      }),
      http.get(apiUrl('/users/me'), () => HttpResponse.json({})),
    );

    const { user } = renderPlanStep();
    await user.click(await screen.findByRole('button', { name: /Annual/ }));
    await selectPlan(user, 'Pro');
    await user.type(within(bar()).getByLabelText('Have a code?'), 'YEAR40');
    await user.click(within(bar()).getByRole('button', { name: 'Apply' }));
    await user.click(within(bar()).getByRole('button', { name: 'Continue to payment' }));

    await vi.waitFor(() => expect(changes).toHaveLength(1));
    expect(changes[0].planCode).toBe('pro');
    expect(changes[0].billingInterval).toBe(BillingInterval.Annual);
    expect(changes[0]).not.toHaveProperty('code');
  });

  it('shows no code field while Free is selected', async () => {
    serve();

    renderPlanStep();

    await screen.findByRole('article', { name: 'Pro' });
    expect(screen.queryByLabelText('Have a code?')).not.toBeInTheDocument();
  });
});

describe('RegisterPlanPage keeping Free', () => {
  /** Counts keep-free POSTs; answers with `respond` (204 unless a test says otherwise). */
  function serveKeepFree(respond: () => Response = () => new HttpResponse(null, { status: 204 })) {
    const calls: string[] = [];
    server.use(
      http.post(apiUrl('/users/me/subscription/keep-free'), () => {
        calls.push('keep-free');
        return respond();
      }),
    );
    return calls;
  }

  it('Start free sends exactly one keep-free request and lands on /profile', async () => {
    serve();
    const calls = serveKeepFree();

    const { user, router } = renderPlanStep();
    const start = await within(await screen.findByRole('region', { name: 'Selected plan' })).findByRole('button', {
      name: 'Start free',
    });
    await user.dblClick(start);

    await vi.waitFor(() => expect(router.state.location.pathname).toBe('/profile'));
    expect(calls).toHaveLength(1);
  });

  it('still lands on /profile, with no error shown, when the request is refused', async () => {
    serve();
    const calls = serveKeepFree(() => problem(409, 'NOT_ON_FREE_PLAN'));

    const { user, router } = renderPlanStep();
    await user.click(await within(await screen.findByRole('region', { name: 'Selected plan' })).findByRole('button', { name: 'Start free' }));

    await vi.waitFor(() => expect(router.state.location.pathname).toBe('/profile'));
    expect(calls).toHaveLength(1);
    expect(useToastStore.getState().toasts).toHaveLength(0);
  });

  it('the plans-load-error "Start free" button records the choice too', async () => {
    server.use(
      http.get(apiUrl('/users/me/subscription'), () => HttpResponse.json(subscription)),
      http.get(apiUrl('/users/me/subscription/plans'), () => HttpResponse.json({}, { status: 500 })),
      http.get(apiUrl('/users/me/roles'), () => HttpResponse.json(roles)),
    );
    const calls = serveKeepFree();

    const { user, router } = renderPlanStep();
    await user.click(await screen.findByRole('button', { name: 'Start free' }));

    await vi.waitFor(() => expect(router.state.location.pathname).toBe('/profile'));
    expect(calls).toHaveLength(1);
  });

  it('choosing a paid plan and continuing sends no keep-free request', async () => {
    serve();
    const calls = serveKeepFree();
    const bodies = serveCheckout();
    vi.stubGlobal('location', { ...window.location, assign: vi.fn() });

    const { user } = renderPlanStep();
    await selectPlan(user, 'Pro');
    await user.click(within(bar()).getByRole('button', { name: 'Continue to payment' }));

    await vi.waitFor(() => expect(bodies).toHaveLength(1));
    expect(calls).toHaveLength(0);
  });
});

describe('RegisterPlanPage code errors', () => {
  async function submitCode(code: string, interval: 'Monthly' | 'Annual' = 'Monthly') {
    const rendered = renderPlanStep();
    const { user } = rendered;
    if (interval === 'Annual') await user.click(await screen.findByRole('button', { name: /Annual/ }));
    await selectPlan(user, 'Pro');
    await user.type(within(bar()).getByLabelText('Have a code?'), code);
    await user.click(within(bar()).getByRole('button', { name: 'Apply' }));
    await user.click(within(bar()).getByRole('button', { name: 'Continue to payment' }));
    return rendered;
  }

  const refusals: Array<[string, number, string, string]> = [
    ['400 PROMOTION_CODE_INVALID', 400, 'PROMOTION_CODE_INVALID', "This code can't be used."],
    ['404 PROMOTION_CODE_NOT_FOUND', 404, 'PROMOTION_CODE_NOT_FOUND', "We don't know this code. Check it and try again."],
    ['409 PROMOTION_CODE_NOT_STARTED', 409, 'PROMOTION_CODE_NOT_STARTED', "This offer hasn't started yet."],
    ['409 PROMOTION_CODE_PLAN_NOT_ELIGIBLE', 409, 'PROMOTION_CODE_PLAN_NOT_ELIGIBLE', "This code doesn't apply to your plan."],
    ['409 PROMOTION_CODE_USED_UP', 409, 'PROMOTION_CODE_USED_UP', 'This code has been used up.'],
    ['409 PROMOTION_CODE_NEW_COACHES_ONLY', 409, 'PROMOTION_CODE_NEW_COACHES_ONLY', 'This code is only for new coaches.'],
    ['409 PROMOTION_CODE_ALREADY_USED', 409, 'PROMOTION_CODE_ALREADY_USED', 'You have already used this code.'],
  ];

  it.each(refusals)('%s shows inline next to the field and keeps the typed code', async (_label, status, code, message) => {
    serve();
    serveCheckout(() => problem(status, code));

    const { router } = await submitCode('BADCODE');

    const alert = await within(bar()).findByRole('alert');
    expect(alert).toHaveTextContent(message);
    const input = within(bar()).getByLabelText('Have a code?');
    expect(input).toHaveValue('BADCODE');
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(input).toHaveAttribute('aria-describedby', alert.id);
    expect(useToastStore.getState().toasts).toHaveLength(0);
    expect(router.state.location.pathname).toBe('/register/plan');
    expect(within(bar()).getByRole('button', { name: 'Continue to payment' })).toBeEnabled();
  });

  it('409 PROMOTION_CODE_INTERVAL_NOT_ELIGIBLE on a monthly checkout says the code is for annual plans only', async () => {
    serve();
    serveCheckout(() => problem(409, 'PROMOTION_CODE_INTERVAL_NOT_ELIGIBLE'));

    await submitCode('YEAR40');

    expect(await within(bar()).findByRole('alert')).toHaveTextContent('This code is for annual plans only.');
    expect(within(bar()).getByLabelText('Have a code?')).toHaveValue('YEAR40');
  });

  it('409 PROMOTION_CODE_INTERVAL_NOT_ELIGIBLE on an annual checkout says the code is for monthly plans only', async () => {
    serve();
    serveCheckout(() => problem(409, 'PROMOTION_CODE_INTERVAL_NOT_ELIGIBLE'));

    await submitCode('MONTH10', 'Annual');

    expect(await within(bar()).findByRole('alert')).toHaveTextContent('This code is for monthly plans only.');
  });

  it('a 429 with a code shows inline, the button works again and editing clears the message', async () => {
    serve();
    serveCheckout(() => HttpResponse.json({}, { status: 429 }));

    const { user } = await submitCode('YEAR40');

    expect(await within(bar()).findByRole('alert')).toHaveTextContent('Too many attempts. Wait a moment and try again.');
    expect(useToastStore.getState().toasts).toHaveLength(0);
    expect(within(bar()).getByRole('button', { name: 'Continue to payment' })).toBeEnabled();

    await user.type(within(bar()).getByLabelText('Have a code?'), '1');
    expect(within(bar()).queryByRole('alert')).not.toBeInTheDocument();
    expect(within(bar()).getByLabelText('Have a code?')).toHaveValue('YEAR401');
  });

  it('a 429 without a code stays a toast', async () => {
    serve();
    serveCheckout(() => HttpResponse.json({}, { status: 429 }));

    const { user } = renderPlanStep();
    await selectPlan(user, 'Pro');
    await user.click(within(bar()).getByRole('button', { name: 'Continue to payment' }));

    await vi.waitFor(() => expect(useToastStore.getState().toasts).toHaveLength(1));
    expect(useToastStore.getState().toasts[0].message).toBe('Too many attempts. Wait a moment and try again.');
    expect(within(bar()).queryByRole('alert')).not.toBeInTheDocument();
  });

  it('PLAN_NOT_PURCHASABLE stays a toast and leaves the code field clean', async () => {
    serve();
    serveCheckout(() => problem(400, 'PLAN_NOT_PURCHASABLE'));

    await submitCode('YEAR40');

    await vi.waitFor(() => expect(useToastStore.getState().toasts).toHaveLength(1));
    expect(useToastStore.getState().toasts[0].message).toBe("This plan can't be bought online right now.");
    expect(within(bar()).getByText('Code YEAR40')).toBeInTheDocument();
    expect(within(bar()).queryByRole('alert')).not.toBeInTheDocument();
  });

  it('a 503 stays a toast', async () => {
    serve();
    serveCheckout(() => HttpResponse.json({ errorCode: 'BILLING_NOT_CONFIGURED' }, { status: 503 }));

    await submitCode('YEAR40');

    await vi.waitFor(() => expect(useToastStore.getState().toasts).toHaveLength(1));
    expect(within(bar()).queryByRole('alert')).not.toBeInTheDocument();
  });
});
