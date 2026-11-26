/**
 * '/plan' — the picker of a coach who is already in the portal: the Monthly | Annual switch, what "Your plan" means
 * when the period differs, the preview and change requests with the billing interval, and the promotion code that
 * only a checkout carries.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import { BillingInterval, BillingTaxMode, PlanChangeTiming, SecondRoleState } from '@/api/generated';
import PlanPickerPage from '@/pages/PlanPickerPage';
import { HttpResponse, apiUrl, http, problem, rfc7807 } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import {
  ANNUAL_PLANS,
  CHECKOUT_URL,
  LAUNCH_OFFER,
  PERIOD_END,
  coachPlan,
  formUpSubscription,
  liveSubscription,
  pinNow,
  plansUnderOffer,
  previewBody,
  renderCoachScreen,
  scheduledChangeSubscription,
  serveChangePlan,
  serveCheckout,
  servePlanPreview,
  servePlanScreens,
  toastMessages,
} from '@/test/planPicker';
import { useCheckoutActivationStore } from '@/stores/checkoutActivation';
import { useToastStore } from '@/stores/toast';

const renderPicker = () => renderCoachScreen(<PlanPickerPage />, '/plan');

const bar = () => screen.getByRole('region', { name: 'Selected plan' });
const annualSwitch = () => screen.findByRole('button', { name: /Annual/ });

async function selectPlan(user: ReturnType<typeof renderPicker>['user'], name: string) {
  const card = await screen.findByRole('article', { name });
  await user.click(within(card).getByRole('button', { name: `Select ${name}` }));
}

afterEach(() => {
  vi.unstubAllGlobals();
  useToastStore.setState({ toasts: [] });
  useCheckoutActivationStore.getState().stop();
});

const crossedOut = (element: HTMLElement) => element.querySelectorAll('s');
const SELECTED_PLAN = { name: 'Selected plan' };
const PROVIDER_DOWN = "The payment provider didn't answer. Please try again in a moment.";

describe('PlanPickerPage billing interval', () => {
  it('shows the switch on the coach own period and says what the coach pays now', async () => {
    servePlanScreens({ subscription: liveSubscription('start', BillingInterval.Monthly) });

    const { user } = renderPicker();

    const group = await screen.findByRole('group', { name: 'Billing period' });
    expect(within(group).getByRole('button', { name: 'Monthly' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.queryByText('You pay Start monthly now.')).not.toBeInTheDocument();

    await user.click(await annualSwitch());

    expect(screen.getByText('You pay Start monthly now.')).toBeInTheDocument();
    const pro = screen.getByRole('article', { name: 'Pro' });
    expect(within(pro).getByText(/5,990/)).toBeInTheDocument();
    expect(within(pro).getByText('/ year')).toBeInTheDocument();
    expect(screen.getByText(/Moving to annual starts today and credits the unused part of your month/)).toBeInTheDocument();
  });

  it('opens on Annual for an annual coach, whose own plan is disabled there but selectable on Monthly', async () => {
    servePlanScreens({ subscription: liveSubscription('start', BillingInterval.Annual) });

    const { user } = renderPicker();

    const start = await screen.findByRole('article', { name: 'Start' });
    expect(within(screen.getByRole('group', { name: 'Billing period' })).getByRole('button', { name: /Annual/ })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(within(start).getByRole('button', { name: 'Your plan' })).toBeDisabled();

    await user.click(screen.getByRole('button', { name: 'Monthly' }));

    expect(screen.getByText('You pay Start annually now.')).toBeInTheDocument();
    expect(within(start).getByRole('button', { name: 'Select Start' })).toBeEnabled();
  });

  it('keeps "Your plan" disabled on the same plan and period, and lets Start annual be chosen from Start monthly', async () => {
    servePlanScreens({ subscription: liveSubscription('start', BillingInterval.Monthly) });

    const { user } = renderPicker();

    const start = await screen.findByRole('article', { name: 'Start' });
    expect(within(start).getByRole('button', { name: 'Your plan' })).toBeDisabled();

    await user.click(await annualSwitch());

    expect(within(start).getByRole('button', { name: 'Select Start' })).toBeEnabled();
  });

  it('hides the switch while no plan is sold annually and the coach pays monthly', async () => {
    servePlanScreens({
      subscription: liveSubscription('start', BillingInterval.Monthly),
      plans: ANNUAL_PLANS.map((plan) => ({ ...plan, annualPriceMinorUnits: undefined })),
    });

    renderPicker();

    expect(await screen.findByRole('article', { name: 'Pro' })).toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'Billing period' })).not.toBeInTheDocument();
  });
});

describe('PlanPickerPage preview and plan change', () => {
  it('sends the interval with the preview and reads again when the switch changes', async () => {
    servePlanScreens({ subscription: liveSubscription('start', BillingInterval.Monthly) });
    const previews = servePlanPreview();

    const { user } = renderPicker();
    await selectPlan(user, 'Pro');
    await vi.waitFor(() => expect(previews).toHaveLength(1));
    expect(previews[0]).toEqual({ planCode: 'pro', billingInterval: BillingInterval.Monthly });

    await user.click(screen.getByRole('button', { name: /Annual/ }));

    await vi.waitFor(() => expect(previews).toHaveLength(2));
    expect(previews[1]).toEqual({ planCode: 'pro', billingInterval: BillingInterval.Annual });
  });

  it('Start monthly to Start annual previews annual, words the credit without an amount and changes with the interval', async () => {
    servePlanScreens({ subscription: liveSubscription('start', BillingInterval.Monthly) });
    const previews = servePlanPreview((body) =>
      HttpResponse.json(
        previewBody({
          planCode: body.planCode,
          billingInterval: BillingInterval.Annual,
          currentBillingInterval: BillingInterval.Monthly,
          todayAmountMinorUnits: 617100,
          nextAmountMinorUnits: 299000,
        }),
      ),
    );
    const changes = serveChangePlan(() => HttpResponse.json({ planCode: 'start', timing: PlanChangeTiming.Now }));

    const { user } = renderPicker();
    await user.click(await annualSwitch());
    await selectPlan(user, 'Start');

    expect(await within(bar()).findByText(/Today about .*6,171.*: the unused part of your Start month is credited\. Renews .*2026/)).toBeInTheDocument();
    expect(previews[0]).toEqual({ planCode: 'start', billingInterval: BillingInterval.Annual });

    await user.click(within(bar()).getByRole('button', { name: 'Switch to Start, annual' }));

    await vi.waitFor(() => expect(changes).toHaveLength(1));
    expect(changes[0].planCode).toBe('start');
    expect(changes[0].billingInterval).toBe(BillingInterval.Annual);
    expect(changes[0]).not.toHaveProperty('code');
  });

  it('an "about" amount in the move-to-annual hint is whole crowns, not hellers', async () => {
    servePlanScreens({ subscription: liveSubscription('start', BillingInterval.Monthly) });
    servePlanPreview((body) =>
      HttpResponse.json(
        previewBody({
          planCode: body.planCode,
          billingInterval: BillingInterval.Annual,
          currentBillingInterval: BillingInterval.Monthly,
          todayAmountMinorUnits: 535104,
          nextAmountMinorUnits: 299000,
        }),
      ),
    );

    const { user } = renderPicker();
    await user.click(await annualSwitch());
    await selectPlan(user, 'Start');

    const hint = await within(bar()).findByText(/Today about .*5,351.*: the unused part/);
    expect(hint).not.toHaveTextContent('5,351.04');
  });

  it('an "about" amount in the upgrade hint is whole crowns too', async () => {
    servePlanScreens({ subscription: liveSubscription('start', BillingInterval.Monthly) });
    servePlanPreview((body) =>
      HttpResponse.json(
        previewBody({
          planCode: body.planCode,
          billingInterval: BillingInterval.Monthly,
          currentBillingInterval: BillingInterval.Monthly,
          todayAmountMinorUnits: 123450,
          nextAmountMinorUnits: 59900,
        }),
      ),
    );

    const { user } = renderPicker();
    await selectPlan(user, 'Pro');

    const hint = await within(bar()).findByText(/Today about/);
    expect(hint).toHaveTextContent(/1,235/);
    expect(hint).not.toHaveTextContent('1,234.50');
  });

  it('annual to monthly says it switches when the paid year ends, with no refund', async () => {
    servePlanScreens({ subscription: liveSubscription('start', BillingInterval.Annual) });
    servePlanPreview((body) =>
      HttpResponse.json(
        previewBody({
          planCode: body.planCode,
          timing: PlanChangeTiming.AtNextRenewal,
          billingInterval: BillingInterval.Monthly,
          currentBillingInterval: BillingInterval.Annual,
          todayAmountMinorUnits: 0,
          nextChargeAt: PERIOD_END,
        }),
      ),
    );

    const { user } = renderPicker();
    await screen.findByRole('article', { name: 'Start' });
    await user.click(screen.getByRole('button', { name: 'Monthly' }));
    await selectPlan(user, 'Start');

    expect(
      await within(bar()).findByText(/Start starts on .*2026, when your paid year ends\. You keep Start until then, with no refund\./),
    ).toBeInTheDocument();
    expect(within(bar()).getByRole('button', { name: 'Switch to Start, monthly' })).toBeEnabled();
  });

  it('an upgrade inside an annual subscription keeps the "rest of this year" wording', async () => {
    servePlanScreens({ subscription: liveSubscription('start', BillingInterval.Annual) });
    servePlanPreview((body) =>
      HttpResponse.json(
        previewBody({
          planCode: body.planCode,
          billingInterval: BillingInterval.Annual,
          currentBillingInterval: BillingInterval.Annual,
          todayAmountMinorUnits: 300000,
          nextAmountMinorUnits: 599000,
        }),
      ),
    );

    const { user } = renderPicker();
    await selectPlan(user, 'Pro');

    expect(await within(bar()).findByText(/Today about .*3,000.* for the rest of this year, then .*5,990.* \/ year from .*2026/)).toBeInTheDocument();
  });

  it('does not preview on the checkout path', async () => {
    servePlanScreens();
    const previews = servePlanPreview();

    const { user } = renderPicker();
    await selectPlan(user, 'Pro');

    expect(within(bar()).getByText("Card and invoices on Stripe's secure pages")).toBeInTheDocument();
    expect(previews).toHaveLength(0);
  });

  it('PLAN_NOT_PURCHASABLE on the preview shows the message in the bar and keeps confirm disabled', async () => {
    servePlanScreens({ subscription: liveSubscription('start', BillingInterval.Monthly) });
    servePlanPreview(() => problem(400, 'PLAN_NOT_PURCHASABLE'));

    const { user } = renderPicker();
    await selectPlan(user, 'Pro');

    expect(await within(bar()).findByText("This plan can't be bought online right now.")).toBeInTheDocument();
    expect(within(bar()).getByRole('button', { name: 'Upgrade to Pro' })).toBeDisabled();
  });

  it('PLAN_NOT_PURCHASABLE on the change is a toast and the bar stays', async () => {
    servePlanScreens({ subscription: liveSubscription('start', BillingInterval.Monthly) });
    servePlanPreview();
    serveChangePlan(() => problem(400, 'PLAN_NOT_PURCHASABLE'));

    const { user } = renderPicker();
    await selectPlan(user, 'Pro');
    await within(bar()).findByText(/Today about/);
    await user.click(within(bar()).getByRole('button', { name: 'Upgrade to Pro' }));

    await vi.waitFor(() => expect(useToastStore.getState().toasts).toHaveLength(1));
    expect(useToastStore.getState().toasts[0].message).toBe("This plan can't be bought online right now.");
    expect(bar()).toBeInTheDocument();
  });

  it('BILLING_PLAN_UNCHANGED from the server is a translated toast', async () => {
    servePlanScreens({ subscription: liveSubscription('start', BillingInterval.Monthly) });
    servePlanPreview();
    serveChangePlan(() => problem(409, 'BILLING_PLAN_UNCHANGED'));

    const { user } = renderPicker();
    await selectPlan(user, 'Pro');
    await within(bar()).findByText(/Today about/);
    await user.click(within(bar()).getByRole('button', { name: 'Upgrade to Pro' }));

    await vi.waitFor(() => expect(useToastStore.getState().toasts).toHaveLength(1));
    expect(useToastStore.getState().toasts[0].message).toBe('You are already on this plan.');
  });
});

describe('PlanPickerPage promotion code', () => {
  it('is offered on the checkout path with the board hint, and rides along with the interval', async () => {
    servePlanScreens();
    const bodies = serveCheckout();
    const assign = vi.fn();
    vi.stubGlobal('location', { ...window.location, assign });

    const { user, router } = renderPicker();
    expect(await screen.findByText('Have a code? Enter it next to the price below. It replaces any current offer.')).toBeInTheDocument();
    await user.click(await annualSwitch());
    await selectPlan(user, 'Pro');
    await user.type(within(bar()).getByLabelText('Have a code?'), '  YEAR40 ');
    await user.click(within(bar()).getByRole('button', { name: 'Apply' }));
    expect(router.state.location.search).toBe('');
    await user.click(within(bar()).getByRole('button', { name: 'Upgrade to Pro, annual' }));

    await vi.waitFor(() => expect(assign).toHaveBeenCalledWith(CHECKOUT_URL));
    expect(bodies).toEqual([
      { planCode: 'pro', billingInterval: BillingInterval.Annual, includeSecondRoleAddOn: false, flow: 'Settings', code: 'YEAR40' },
    ]);
  });

  it('is absent for a live paid subscription, with no promise of a code at checkout', async () => {
    servePlanScreens({ subscription: liveSubscription('start', BillingInterval.Monthly) });
    servePlanPreview();

    const { user } = renderPicker();
    await selectPlan(user, 'Pro');
    await within(bar()).findByText(/Today about/);

    expect(screen.queryByLabelText('Have a code?')).not.toBeInTheDocument();
    expect(screen.queryByText(/Have a code\?/)).not.toBeInTheDocument();
  });

  it('is absent while Free is selected', async () => {
    servePlanScreens({ subscription: liveSubscription('start', BillingInterval.Monthly) });

    const { user } = renderPicker();
    await selectPlan(user, 'Free');

    expect(screen.queryByLabelText('Have a code?')).not.toBeInTheDocument();
  });

  describe('refusals', () => {
    async function checkoutWithCode(interval: 'Monthly' | 'Annual' = 'Monthly') {
      const rendered = renderPicker();
      const { user } = rendered;
      if (interval === 'Annual') await user.click(await annualSwitch());
      await selectPlan(user, 'Pro');
      await user.type(within(bar()).getByLabelText('Have a code?'), 'BADCODE');
      await user.click(within(bar()).getByRole('button', { name: 'Apply' }));
      await user.click(within(bar()).getByRole('button', { name: interval === 'Annual' ? 'Upgrade to Pro, annual' : 'Upgrade to Pro' }));
      return rendered;
    }

    const refusals: Array<[string, number, string, string]> = [
      ['400 PROMOTION_CODE_INVALID', 400, 'PROMOTION_CODE_INVALID', "This code can't be used."],
      ['404 PROMOTION_CODE_NOT_FOUND', 404, 'PROMOTION_CODE_NOT_FOUND', "We don't know this code. Check it and try again."],
    ];

    it.each(refusals)('%s shows at the code field, keeps the code and stays on /plan', async (_label, status, code, message) => {
      servePlanScreens();
      serveCheckout(() => problem(status, code));

      const { router } = await checkoutWithCode();

      const alert = await within(bar()).findByRole('alert');
      expect(alert).toHaveTextContent(message);
      expect(within(bar()).getByLabelText('Have a code?')).toHaveValue('BADCODE');
      expect(useToastStore.getState().toasts).toHaveLength(0);
      expect(router.state.location.pathname).toBe('/plan');
    });

    it('409 PROMOTION_CODE_INTERVAL_NOT_ELIGIBLE on annual says the code is for monthly plans only', async () => {
      servePlanScreens();
      serveCheckout(() => problem(409, 'PROMOTION_CODE_INTERVAL_NOT_ELIGIBLE'));

      const { router } = await checkoutWithCode('Annual');

      expect(await within(bar()).findByRole('alert')).toHaveTextContent('This code is for monthly plans only.');
      expect(router.state.location.pathname).toBe('/plan');
    });

    it('409 PROMOTION_CODE_INTERVAL_NOT_ELIGIBLE on monthly says the code is for annual plans only', async () => {
      servePlanScreens();
      serveCheckout(() => problem(409, 'PROMOTION_CODE_INTERVAL_NOT_ELIGIBLE'));

      await checkoutWithCode();

      expect(await within(bar()).findByRole('alert')).toHaveTextContent('This code is for annual plans only.');
    });

    it('a 429 while probing codes shows at the field and keeps the typed code', async () => {
      servePlanScreens();
      serveCheckout(() => HttpResponse.json({}, { status: 429 }));

      await checkoutWithCode();

      expect(await within(bar()).findByRole('alert')).toHaveTextContent('Too many attempts. Wait a moment and try again.');
      expect(within(bar()).getByLabelText('Have a code?')).toHaveValue('BADCODE');
      expect(within(bar()).getByRole('button', { name: 'Upgrade to Pro' })).toBeEnabled();
    });

    it('switching the period clears the refusal', async () => {
      servePlanScreens();
      serveCheckout(() => problem(409, 'PROMOTION_CODE_INTERVAL_NOT_ELIGIBLE'));

      const { user } = await checkoutWithCode();
      await within(bar()).findByRole('alert');
      await user.click(screen.getByRole('button', { name: /Annual/ }));

      expect(within(bar()).queryByRole('alert')).not.toBeInTheDocument();
    });
  });
});

describe('PlanPickerPage plan list', () => {
  it('a plan without an annual price keeps its monthly price on Annual', async () => {
    servePlanScreens({
      plans: [...ANNUAL_PLANS.slice(0, 3), coachPlan({ code: 'studio', nameEn: 'Studio', priceMinorUnits: 99900 })],
    });

    const { user } = renderPicker();
    await user.click(await annualSwitch());

    const studio = screen.getByRole('article', { name: 'Studio' });
    expect(within(studio).getByText('/ month')).toBeInTheDocument();
    expect(within(screen.getByRole('article', { name: 'Start' })).getByText('/ year')).toBeInTheDocument();
  });
});

describe('PlanPickerPage plan cards', () => {
  it('shows four plans, outlines the current one and shows the VAT line for Vat21', async () => {
    servePlanScreens({ taxMode: BillingTaxMode.Vat21 });

    renderPicker();

    expect(await screen.findByRole('heading', { name: 'Choose your plan', level: 1 })).toBeInTheDocument();
    for (const name of ['Free', 'Start', 'Pro', 'Studio']) {
      expect(await screen.findByRole('article', { name })).toBeInTheDocument();
    }
    const free = screen.getByRole('article', { name: 'Free' });
    expect(within(free).getAllByText('Your plan', { exact: true }).length).toBeGreaterThan(0);
    expect(within(free).getByRole('button', { name: 'Your plan' })).toBeDisabled();
    expect(within(screen.getByRole('article', { name: 'Pro' })).getByRole('button', { name: 'Select Pro' })).toBeEnabled();
    expect(screen.getByRole('link', { name: 'Back to settings' })).toBeInTheDocument();
    expect(screen.getByText('Prices without VAT, 21 % VAT is added.')).toBeInTheDocument();
  });

  it('shows no VAT line when the server tax mode is None', async () => {
    servePlanScreens({ taxMode: BillingTaxMode.None });

    renderPicker();

    expect(await screen.findByRole('article', { name: 'Pro' })).toBeInTheDocument();
    expect(screen.queryByText(/Prices without VAT/)).not.toBeInTheDocument();
  });

  it('shows no banner and no crossed-out price when no promotion applies', async () => {
    servePlanScreens({ taxMode: BillingTaxMode.Vat21 });

    renderPicker();

    expect(await screen.findByRole('article', { name: 'Pro' })).toBeInTheDocument();
    expect(screen.queryByText(/Launch offer/)).not.toBeInTheDocument();
    expect(document.querySelectorAll('article s')).toHaveLength(0);
  });

  it('an automatic promotion shows the banner and crossed-out prices on every paid plan', async () => {
    pinNow();
    servePlanScreens({ taxMode: BillingTaxMode.Vat21, plans: plansUnderOffer('all'), promotion: LAUNCH_OFFER });

    renderPicker();

    expect(await screen.findByText('Launch offer: −30 % for your first 3 months')).toBeInTheDocument();
    expect(screen.getByText(/Applied at checkout on every paid plan\. Claim it by .*2026\./)).toBeInTheDocument();
    for (const name of ['Start', 'Pro', 'Studio']) {
      const card = screen.getByRole('article', { name });
      expect(crossedOut(card)).toHaveLength(1);
      expect(within(card).getByText('−30 % for 3 months')).toBeInTheDocument();
    }
    // The server's discounted price is the big one; the plan's own price is the crossed-out one.
    const pro = screen.getByRole('article', { name: 'Pro' });
    expect(crossedOut(pro)[0]).toHaveTextContent('599');
    expect(within(pro).getByText(/419/)).toBeInTheDocument();
    // Free is never discounted.
    expect(crossedOut(screen.getByRole('article', { name: 'Free' }))).toHaveLength(0);
  });

  it('a promotion that covers only Pro crosses out only the Pro price and names the plan', async () => {
    pinNow();
    servePlanScreens({
      taxMode: BillingTaxMode.Vat21,
      plans: plansUnderOffer('proOnly'),
      promotion: { ...LAUNCH_OFFER, planCodes: ['pro'] },
    });

    renderPicker();

    expect(await screen.findByText(/Applied at checkout on Pro\./)).toBeInTheDocument();
    expect(crossedOut(screen.getByRole('article', { name: 'Pro' }))).toHaveLength(1);
    expect(crossedOut(screen.getByRole('article', { name: 'Start' }))).toHaveLength(0);
    expect(crossedOut(screen.getByRole('article', { name: 'Studio' }))).toHaveLength(0);
  });

  it('the back icon button is icon-only and opens Settings at Plan and billing', async () => {
    servePlanScreens();

    const { user, router } = renderPicker();

    const back = await screen.findByRole('link', { name: 'Back to settings' });
    expect(back).toHaveAttribute('href', '/settings#plan-billing');
    expect(back).toHaveTextContent('');

    await user.click(back);

    expect(router.state.location.pathname).toBe('/settings');
    expect(router.state.location.hash).toBe('#plan-billing');
  });
});

describe('PlanPickerPage selecting a plan', () => {
  it('a paid plan posts the checkout request and follows the returned URL', async () => {
    servePlanScreens();
    const bodies = serveCheckout();
    const assign = vi.fn();
    vi.stubGlobal('location', { ...window.location, assign });

    const { user } = renderPicker();
    await selectPlan(user, 'Pro');
    await user.click(within(bar()).getByRole('button', { name: 'Upgrade to Pro' }));

    await vi.waitFor(() => expect(assign).toHaveBeenCalledWith(CHECKOUT_URL));
    expect(bodies).toEqual([{ planCode: 'pro', billingInterval: BillingInterval.Monthly, includeSecondRoleAddOn: false, flow: 'Settings' }]);
  });

  it('billing not configured shows a localized toast and stays on the picker', async () => {
    servePlanScreens();
    serveCheckout(() => rfc7807(503, 'BILLING_NOT_CONFIGURED'));

    const { user, router } = renderPicker();
    await selectPlan(user, 'Start');
    await user.click(within(bar()).getByRole('button', { name: 'Upgrade to Start' }));

    await vi.waitFor(() => expect(toastMessages()).toContain("Billing isn't available right now. Please try again later."));
    expect(router.state.location.pathname).toBe('/plan');
    expect(within(screen.getByRole('article', { name: 'Start' })).getByRole('button', { name: 'Selected' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(within(bar()).getByRole('button', { name: 'Upgrade to Start' })).toBeEnabled();
  });

  it('selecting a card, by its button or its body, starts nothing until the bar is confirmed', async () => {
    servePlanScreens();
    const billingCalls: string[] = [];
    const record = (name: string) => () => {
      billingCalls.push(name);
      return HttpResponse.json({}, { status: 500 });
    };
    server.use(
      http.post(apiUrl('/billing/checkout-session'), record('checkout-session')),
      http.post(apiUrl('/billing/change-plan'), record('change-plan')),
      http.post(apiUrl('/billing/switch-to-free'), record('switch-to-free')),
    );

    const { user } = renderPicker();
    const pro = await screen.findByRole('article', { name: 'Pro' });
    // Nothing is preselected on /plan, so there is no bar.
    expect(screen.queryByRole('region', SELECTED_PLAN)).not.toBeInTheDocument();

    await user.click(within(pro).getByRole('button', { name: 'Select Pro' }));
    expect(within(bar()).getByText(/Pro · /)).toBeInTheDocument();
    expect(within(pro).getByRole('button', { name: 'Selected' })).toHaveAttribute('aria-pressed', 'true');

    // A click on the card body moves the selection and still starts nothing.
    const studio = screen.getByRole('article', { name: 'Studio' });
    await user.click(within(studio).getByRole('heading', { name: 'Studio' }));
    expect(within(studio).getByRole('button', { name: 'Selected' })).toBeInTheDocument();
    expect(within(pro).getByRole('button', { name: 'Select Pro' })).toHaveAttribute('aria-pressed', 'false');
    expect(within(bar()).getByRole('button', { name: 'Upgrade to Studio' })).toBeEnabled();
    expect(billingCalls).toEqual([]);
  });

  it('the current plan is not selectable and the bar spells out the promotion price', async () => {
    pinNow();
    servePlanScreens({ taxMode: BillingTaxMode.Vat21, plans: plansUnderOffer('all'), promotion: LAUNCH_OFFER });

    const { user } = renderPicker();
    const free = await screen.findByRole('article', { name: 'Free' });
    expect(within(free).getByRole('button', { name: 'Your plan' })).toBeDisabled();
    await user.click(within(free).getByRole('heading', { name: 'Free' }));
    expect(screen.queryByRole('region', SELECTED_PLAN)).not.toBeInTheDocument();

    await selectPlan(user, 'Pro');
    expect(within(bar()).getByText(/Pro · .*419.* \/ month/)).toBeInTheDocument();
    expect(within(bar()).getByText(/−30 % for 3 months, then .*599/)).toBeInTheDocument();
    expect(within(bar()).getByText("Card and invoices on Stripe's secure pages")).toBeInTheDocument();
  });
});

describe('PlanPickerPage changing a live paid plan', () => {
  it("an upgrade shows today's charge from the preview and sends one attempt id per attempt", async () => {
    servePlanScreens({ subscription: liveSubscription('start', BillingInterval.Monthly, { secondRole: SecondRoleState.NotAvailable }) });
    const previews = servePlanPreview();
    let attempts = 0;
    const changes = serveChangePlan(() =>
      ++attempts === 1 ? rfc7807(502, 'BILLING_PROVIDER_ERROR') : HttpResponse.json({ planCode: 'pro', timing: PlanChangeTiming.Now }),
    );

    const { user } = renderPicker();
    await selectPlan(user, 'Pro');
    expect(await within(bar()).findByText(/Today about .*158.* for the rest of this month, then .*599.* \/ month from .*2026/)).toBeInTheDocument();
    expect(within(bar()).queryByText('Your switch to Free will be cancelled.')).not.toBeInTheDocument();
    expect(previews).toEqual([{ planCode: 'pro', billingInterval: BillingInterval.Monthly }]);

    // A declined charge ends that attempt: the retry carries a new id.
    await user.click(within(bar()).getByRole('button', { name: 'Upgrade to Pro' }));
    await vi.waitFor(() => expect(toastMessages()).toContain(PROVIDER_DOWN));
    await user.click(within(bar()).getByRole('button', { name: 'Upgrade to Pro' }));
    await vi.waitFor(() => expect(changes).toHaveLength(2));
    expect(changes[0].planCode).toBe('pro');
    expect(changes[0].attemptId).toMatch(/^[0-9a-f-]{36}$/);
    expect(changes[1].attemptId).toMatch(/^[0-9a-f-]{36}$/);
    expect(changes[1].attemptId).not.toBe(changes[0].attemptId);
  });

  it('an upgrade that needs payment shows a link to Stripe, keeps the old plan and starts the watch on click', async () => {
    const paymentUrl = 'https://invoice.stripe.test/i/acct_test/test_invoice';
    servePlanScreens({ subscription: liveSubscription('start', BillingInterval.Monthly, { secondRole: SecondRoleState.NotAvailable }) });
    servePlanPreview();
    serveChangePlan(() => HttpResponse.json({ planCode: 'pro', timing: PlanChangeTiming.AwaitingPayment, paymentUrl }));
    const open = vi.fn();
    vi.stubGlobal('open', open);

    const { user } = renderPicker();
    await selectPlan(user, 'Pro');
    await user.click(within(bar()).getByRole('button', { name: 'Upgrade to Pro' }));

    const notice = await screen.findByTestId('upgrade-payment-notice');
    const link = within(notice).getByRole('link', { name: 'Continue to payment' });
    expect(link).toHaveAttribute('href', paymentUrl);
    expect(link).toHaveAttribute('target', '_blank');
    expect(link).toHaveAttribute('rel', expect.stringContaining('noopener'));
    // No tab opens by itself, no "changed" toast, and the page still shows the old plan.
    expect(open).not.toHaveBeenCalled();
    expect(toastMessages().filter((message) => /You are now on the/.test(message))).toEqual([]);
    expect(within(screen.getByRole('article', { name: 'Start' })).getByRole('button', { name: 'Your plan' })).toBeDisabled();
    expect(sessionStorage.getItem('checkoutActivationWatch')).toBeNull();

    // The click is what opens the payment page and starts the watch for the Pro plan.
    await user.click(link);
    expect(sessionStorage.getItem('checkoutActivationWatch')).toContain('"targetPlanCode":"pro"');
  });

  it('the bar shows an error hint when the preview fails and confirming still works', async () => {
    servePlanScreens({ subscription: liveSubscription('start', BillingInterval.Monthly, { secondRole: SecondRoleState.NotAvailable }) });
    servePlanPreview(() => rfc7807(502, 'BILLING_PROVIDER_ERROR'));

    const { user } = renderPicker();
    await selectPlan(user, 'Pro');

    expect(await within(bar()).findByText("Couldn't calculate today's charge. You can still confirm the change.")).toBeInTheDocument();
    expect(within(bar()).queryByText(/Today about/)).not.toBeInTheDocument();
    expect(within(bar()).queryByText('Your switch to Free will be cancelled.')).not.toBeInTheDocument();
    expect(within(bar()).getByRole('button', { name: 'Upgrade to Pro' })).toBeEnabled();
  });

  it('an upgrade that drops a scheduled switch to Free says so in the bar', async () => {
    servePlanScreens({
      subscription: liveSubscription('start', BillingInterval.Monthly, {
        secondRole: SecondRoleState.NotAvailable,
        cancelAtPeriodEnd: true,
        scheduledPlan: { planCode: 'free', nameEn: 'Free', startsAt: PERIOD_END, priceLines: [] },
      }),
    });
    servePlanPreview((body) => HttpResponse.json(previewBody({ planCode: body.planCode, cancelsScheduledSwitchToFree: true })));

    const { user } = renderPicker();
    await selectPlan(user, 'Pro');

    expect(await within(bar()).findByText(/Today about .*158/)).toBeInTheDocument();
    expect(within(bar()).getByText('Your switch to Free will be cancelled.')).toBeInTheDocument();
  });

  it('a downgrade and a switch to Free say when they start and what the coach keeps until then', async () => {
    servePlanScreens({ subscription: liveSubscription('pro', BillingInterval.Monthly, { secondRole: SecondRoleState.NotAvailable }) });
    servePlanPreview(() =>
      HttpResponse.json(previewBody({ planCode: 'start', timing: PlanChangeTiming.AtNextRenewal, todayAmountMinorUnits: 0, nextAmountMinorUnits: 29900 })),
    );

    const { user } = renderPicker();
    await selectPlan(user, 'Start');
    expect(await within(bar()).findByText(/Start starts on .*2026\. You keep Pro until then\./)).toBeInTheDocument();
    expect(within(bar()).queryByText(/Today about/)).not.toBeInTheDocument();

    await user.click(within(screen.getByRole('article', { name: 'Free' })).getByRole('button', { name: /Select Free/ }));
    expect(await within(bar()).findByText(/Free starts on .*2026\. You keep Pro until then\./)).toBeInTheDocument();
  });
});

describe('PlanPickerPage a plan set by Form Up', () => {
  const GIFT_PLANS = plansUnderOffer('all');

  it('the current card says who provides the plan until when, with no price', async () => {
    pinNow();
    servePlanScreens({ subscription: formUpSubscription('2099-01-31T12:00:00Z'), plans: GIFT_PLANS, promotion: LAUNCH_OFFER });

    renderPicker();

    const pro = await screen.findByRole('article', { name: 'Pro' });
    expect(await within(pro).findByText(/^Provided by Form Up until .*2099/)).toBeInTheDocument();
    expect(crossedOut(pro)).toHaveLength(0);
    // The price block stays in the DOM as a hidden spacer that keeps the card height. jsdom has no stylesheet, so
    // "hidden" is the aria-hidden wrapper the spacer carries.
    for (const price of within(pro).queryAllByText(/599|419/)) {
      expect(price.closest('[aria-hidden="true"]')).not.toBeNull();
    }
    expect(within(pro).getByRole('button', { name: 'Your plan' })).toBeDisabled();
    // Other cards keep their price and promotion.
    expect(crossedOut(screen.getByRole('article', { name: 'Studio' }))).toHaveLength(1);
  });

  it('without a future end date the card uses the undated wording', async () => {
    pinNow();
    servePlanScreens({ subscription: formUpSubscription('2020-01-31T12:00:00Z') });

    renderPicker();

    const pro = await screen.findByRole('article', { name: 'Pro' });
    expect(await within(pro).findByText('Provided by Form Up', { exact: true })).toBeInTheDocument();
  });

  it('any paid plan shows the warning in the bar, a higher or a cheaper one', async () => {
    pinNow();
    servePlanScreens({ subscription: formUpSubscription('2099-01-31T12:00:00Z') });

    const { user } = renderPicker();
    await selectPlan(user, 'Studio');
    expect(within(bar()).getByText('Your plan from Form Up will end and you will pay for Studio from today.')).toBeInTheDocument();
    expect(within(bar()).getByText("Card and invoices on Stripe's secure pages")).toBeInTheDocument();

    await selectPlan(user, 'Start');
    expect(within(bar()).getByText('Your plan from Form Up will end and you will pay for Start from today.')).toBeInTheDocument();
    expect(within(bar()).queryByText('Takes effect at the end of the paid month')).not.toBeInTheDocument();
  });

  it('a coach on Free sees neither the Form Up card text nor the warning', async () => {
    servePlanScreens();

    const { user } = renderPicker();
    await selectPlan(user, 'Start');

    expect(screen.queryByText(/Provided by Form Up/)).not.toBeInTheDocument();
    expect(screen.queryByText(/plan from Form Up will end/)).not.toBeInTheDocument();
  });
});

describe('PlanPickerPage cancelling a scheduled change', () => {
  /** Answers the cancel with `respond`; the coach's subscription then reads as live, without the scheduled change. */
  function serveCancel(respond: () => Response) {
    const calls: string[] = [];
    server.use(
      http.post(apiUrl('/billing/scheduled-change/cancel'), () => {
        calls.push('POST');
        servePlanScreens({ subscription: liveSubscription('pro', BillingInterval.Monthly, { secondRole: SecondRoleState.NotAvailable }) });
        return respond();
      }),
      http.get(apiUrl('/users/me'), () => HttpResponse.json({})),
    );
    return calls;
  }

  it('keeps Pro over a scheduled switch to Free: Free stays disabled, no preview call, one cancel POST', async () => {
    servePlanScreens({ subscription: scheduledChangeSubscription('free', true) });
    const previews = servePlanPreview(() => HttpResponse.json({}, { status: 500 }));
    const cancels = serveCancel(() => HttpResponse.json({ kind: 'SwitchToFree', planCode: 'free' }));

    const { user } = renderPicker();
    const pro = await screen.findByRole('article', { name: 'Pro' });
    expect(within(pro).getAllByText('Your plan', { exact: true }).length).toBeGreaterThan(0);
    expect(within(screen.getByRole('article', { name: 'Free' })).getByRole('button', { name: /Switches to Free on .*2026/ })).toBeDisabled();

    await user.click(within(pro).getByRole('button', { name: 'Select Pro' }));
    expect(within(bar()).getByText(/Cancels your switch to Free on .*2026\. You stay on Pro, nothing is charged now\./)).toBeInTheDocument();
    expect(previews).toEqual([]);

    await user.click(within(bar()).getByRole('button', { name: 'Keep Pro' }));

    await vi.waitFor(() => expect(toastMessages()).toContain('Change cancelled. You stay on Pro.'));
    await vi.waitFor(() => expect(screen.queryByRole('region', SELECTED_PLAN)).not.toBeInTheDocument());
    await vi.waitFor(() => expect(within(pro).getByRole('button', { name: 'Your plan' })).toBeDisabled());
    expect(cancels).toEqual(['POST']);
    expect(previews).toEqual([]);
  });

  it('keeps Pro over a scheduled downgrade: the target card is disabled and the bar names it', async () => {
    servePlanScreens({ subscription: scheduledChangeSubscription('start', true) });
    serveCancel(() => HttpResponse.json({ kind: 'Downgrade', planCode: 'start', renewsAt: PERIOD_END }));

    const { user } = renderPicker();
    const start = await screen.findByRole('article', { name: 'Start' });
    expect(within(start).getByRole('button', { name: /Switches to Start on .*2026/ })).toBeDisabled();

    await selectPlan(user, 'Pro');
    expect(within(bar()).getByText(/Cancels your switch to Start on .*2026\. You stay on Pro/)).toBeInTheDocument();
    await user.click(within(bar()).getByRole('button', { name: 'Keep Pro' }));

    await vi.waitFor(() => expect(toastMessages()).toContain('Change cancelled. You stay on Pro.'));
    await vi.waitFor(() => expect(within(start).getByRole('button', { name: 'Select Start' })).toBeEnabled());
  });

  it('a change that cannot be cancelled leaves the current card unselectable', async () => {
    servePlanScreens({ subscription: scheduledChangeSubscription('start', false) });

    renderPicker();

    const pro = await screen.findByRole('article', { name: 'Pro' });
    expect(within(pro).getByRole('button', { name: 'Your plan' })).toBeDisabled();
    expect(screen.queryByRole('button', { name: /Keep Pro/ })).not.toBeInTheDocument();
  });

  it('a 409 from a stale tab shows its message', async () => {
    servePlanScreens({ subscription: scheduledChangeSubscription('free', true) });
    serveCancel(() => rfc7807(409, 'BILLING_RENEWAL_CANCELLED_BY_ADMIN'));

    const { user } = renderPicker();
    await selectPlan(user, 'Pro');
    await user.click(within(bar()).getByRole('button', { name: 'Keep Pro' }));

    await vi.waitFor(() => expect(toastMessages()).toContain('Form Up cancelled the renewal of your plan. Contact us to change this.'));
  });
});
