/**
 * '/plan' — the picker of a coach who is already in the portal: the Monthly | Annual switch, what "Your plan" means
 * when the period differs, the preview and change requests with the billing interval, and the promotion code that
 * only a checkout carries.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import { BillingInterval, PlanChangeTiming } from '@/api/generated';
import PlanPickerPage from '@/pages/PlanPickerPage';
import { HttpResponse, problem } from '@/test/msw/handlers';
import {
  ANNUAL_PLANS,
  CHECKOUT_URL,
  PERIOD_END,
  coachPlan,
  liveSubscription,
  previewBody,
  renderCoachScreen,
  serveChangePlan,
  serveCheckout,
  servePlanPreview,
  servePlanScreens,
} from '@/test/planPicker';
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
});

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
