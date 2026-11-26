/**
 * Settings > Plan and billing — the card follows the billing interval the subscription reports: the renewal line,
 * the bill title and totals, the next-year amount, and the scheduled-change and add-role texts.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import {
  BillingInterval,
  PromotionBillingPeriod,
  PromotionDuration,
  PromotionKind,
  SecondRoleState,
  SubscriptionPriceLineKind,
} from '@/api/generated';
import type { GetMySubscriptionResponse, ScheduledPlanDto } from '@/api/generated';
import PlanBillingCard from '@/components/settings/PlanBillingCard';
import { PERIOD_END, liveSubscription, renderCoachScreen, servePlanScreens, toastMessages } from '@/test/planPicker';
import { HttpResponse, apiUrl, http, rfc7807 } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { useToastStore } from '@/stores/toast';

const renderCard = () => renderCoachScreen(<PlanBillingCard />, '/settings');

const planLine = (amount: number) => ({ kind: SubscriptionPriceLineKind.Plan, amountMinorUnits: amount, currency: 'CZK' });
const addOnLine = (amount: number) => ({ kind: SubscriptionPriceLineKind.SecondRoleAddOn, amountMinorUnits: amount, currency: 'CZK' });

describe('PlanBillingCard billing interval', () => {
  it('an annual subscription reads "Annual · Renews", "Your annual bill" and totals per year', async () => {
    servePlanScreens({
      subscription: liveSubscription('pro', BillingInterval.Annual, {
        priceLines: [planLine(599000), addOnLine(299000)],
      }),
    });

    renderCard();

    expect(await screen.findByText(/^Annual · Renews .*2026$/)).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Your annual bill' })).toBeInTheDocument();
    expect(screen.getByText(/^CZK 8,980 \/ year$/)).toBeInTheDocument();
    expect(screen.queryByText(/\/ month/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Monthly/)).not.toBeInTheDocument();
    // Next year costs the same here, and the way to pay monthly is named.
    expect(screen.getByText(/From .*2026: .*8,980.* \/ year\. To pay monthly, change your plan; it switches when this year ends\./)).toBeInTheDocument();
  });

  it('an annual first-year offer shows the discounted total and the full price from the end date', async () => {
    servePlanScreens({
      subscription: liveSubscription('pro', BillingInterval.Annual, {
        priceLines: [planLine(599000), addOnLine(299000)],
        promotion: {
          name: 'Launch offer',
          kind: PromotionKind.Percent,
          value: 30,
          duration: PromotionDuration.Months,
          durationMonths: 12,
          billingPeriod: PromotionBillingPeriod.Annual,
          endsAt: PERIOD_END,
          discountMinorUnits: 269400,
          discountedTotalMinorUnits: 628600,
        },
      }),
    });

    renderCard();

    expect(await screen.findByText(/^CZK 6,286 \/ year$/)).toBeInTheDocument();
    expect(screen.getByText(/^−CZK 2,694$/)).toBeInTheDocument();
    expect(screen.getByText(/From .*2026: .*8,980.* \/ year\./)).toBeInTheDocument();
  });

  it('an annual subscription with an N-month promotion labels it as the first year and names the renewal, not the discount end', async () => {
    const discountEnd = '2027-02-19T00:00:00Z';
    servePlanScreens({
      subscription: liveSubscription('pro', BillingInterval.Annual, {
        priceLines: [planLine(599000)],
        promotion: {
          name: 'Úvodní nabídka',
          nameEn: 'Launch offer',
          kind: PromotionKind.Percent,
          value: 30,
          duration: PromotionDuration.Months,
          durationMonths: 3,
          billingPeriod: PromotionBillingPeriod.Monthly,
          endsAt: discountEnd,
          discountMinorUnits: 179700,
          discountedTotalMinorUnits: 419300,
        },
      }),
    });

    renderCard();

    // The UI language is English, so the English name shows instead of the Czech one.
    expect(await screen.findByText(/Launch offer .* · first year/)).toBeInTheDocument();
    expect(screen.queryByText(/Úvodní nabídka/)).not.toBeInTheDocument();
    expect(screen.queryByText(/months? left/)).not.toBeInTheDocument();
    const renewal = new Intl.DateTimeFormat('en', { dateStyle: 'medium' }).format(new Date(PERIOD_END));
    const discountEndText = new Intl.DateTimeFormat('en', { dateStyle: 'medium' }).format(new Date(discountEnd));
    expect(screen.getByText(new RegExp(`From ${renewal}: .*5,990.* / year\\.`))).toBeInTheDocument();
    expect(screen.queryByText(new RegExp(`From ${discountEndText}`))).not.toBeInTheDocument();
  });

  it('an annual subscription with a scheduled plan change shows no next-year line', async () => {
    servePlanScreens({
      subscription: liveSubscription('pro', BillingInterval.Annual, {
        priceLines: [planLine(599000)],
        scheduledPlan: {
          planCode: 'start',
          nameEn: 'Start',
          startsAt: PERIOD_END,
          cancellable: true,
          billingInterval: BillingInterval.Monthly,
          priceLines: [planLine(29900)],
        },
      }),
    });

    renderCard();

    expect(await screen.findByText(/Start begins on/)).toBeInTheDocument();
    expect(screen.queryByText(/From .*2026: .* \/ year/)).not.toBeInTheDocument();
    expect(screen.queryByText(/To pay monthly/)).not.toBeInTheDocument();
  });

  it('a monthly subscription keeps "Monthly · Renews", "Your monthly bill" and no annual note', async () => {
    servePlanScreens({ subscription: liveSubscription('start', BillingInterval.Monthly) });

    renderCard();

    expect(await screen.findByText(/^Monthly · Renews .*2026$/)).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Your monthly bill' })).toBeInTheDocument();
    expect(screen.getByText(/299.* \/ month/)).toBeInTheDocument();
    expect(screen.queryByText(/To pay monthly/)).not.toBeInTheDocument();
    expect(screen.queryByText(/\/ year/)).not.toBeInTheDocument();
    expect(screen.queryByText(/ begins on /)).not.toBeInTheDocument();
  });

  it('a scheduled monthly plan after the paid year is priced per month and the annual note steps aside', async () => {
    servePlanScreens({
      subscription: liveSubscription('pro', BillingInterval.Annual, {
        priceLines: [planLine(599000)],
        scheduledPlan: {
          planCode: 'start',
          nameEn: 'Start',
          startsAt: PERIOD_END,
          cancellable: true,
          billingInterval: BillingInterval.Monthly,
          priceLines: [planLine(29900)],
        },
      }),
    });

    renderCard();

    expect(await screen.findByText(/^Annual · Pro until .*2026$/)).toBeInTheDocument();
    expect(screen.getByText(/Start begins on .*2026 · .*299.* \/ month/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Cancel change' })).toBeInTheDocument();
    expect(screen.queryByText(/To pay monthly/)).not.toBeInTheDocument();
  });

  it('a scheduled annual plan is priced per year', async () => {
    servePlanScreens({
      subscription: liveSubscription('pro', BillingInterval.Annual, {
        priceLines: [planLine(599000)],
        scheduledPlan: {
          planCode: 'start',
          nameEn: 'Start',
          startsAt: PERIOD_END,
          cancellable: true,
          billingInterval: BillingInterval.Annual,
          priceLines: [planLine(299000)],
        },
      }),
    });

    renderCard();

    expect(await screen.findByText(/Start begins on .*2026 · .*2,990.* \/ year/)).toBeInTheDocument();
  });

  it('the end of the second-role add-on names the new annual payment on an annual subscription', async () => {
    servePlanScreens({
      subscription: liveSubscription('pro', BillingInterval.Annual, {
        priceLines: [planLine(599000), addOnLine(299000)],
        secondRoleAddOnEndsAt: PERIOD_END,
      }),
    });

    renderCard();

    const line = await screen.findByText(/The second role stops being billed on/);
    expect(line).toHaveTextContent(/New annual payment: .*5,990/);
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  useToastStore.setState({ toasts: [] });
});

/** A coach on a monthly Pro with no second role; `overrides` add the scheduled change or the add-on under test. */
const proSubscription = (overrides: Partial<GetMySubscriptionResponse> = {}) =>
  liveSubscription('pro', BillingInterval.Monthly, {
    secondRole: SecondRoleState.NotAvailable,
    priceLines: [planLine(59900)],
    ...overrides,
  });

const scheduled = (planCode: string, nameEn: string, overrides: Partial<ScheduledPlanDto> = {}): ScheduledPlanDto => ({
  planCode,
  nameEn,
  startsAt: PERIOD_END,
  billingInterval: BillingInterval.Monthly,
  priceLines: [],
  ...overrides,
});

describe('PlanBillingCard with a scheduled change', () => {
  it('a scheduled downgrade shows the current plan until the date and the next plan with its price', async () => {
    servePlanScreens({ subscription: proSubscription({ scheduledPlan: scheduled('start', 'Start', { priceLines: [planLine(29900)] }) }) });

    renderCard();

    expect(await screen.findByText(/Monthly · Pro until .*2026/)).toBeInTheDocument();
    expect(screen.getByText(/Start begins on .*2026 · .*299.* \/ month/)).toBeInTheDocument();
    expect(screen.queryByText(/^Ends /)).not.toBeInTheDocument();
  });

  it('a scheduled switch to Free shows when Free begins, without a price', async () => {
    servePlanScreens({ subscription: proSubscription({ cancelAtPeriodEnd: true, scheduledPlan: scheduled('free', 'Free') }) });

    renderCard();

    expect(await screen.findByText(/Monthly · Pro until .*2026/)).toBeInTheDocument();
    expect(screen.getByText(/^Free begins on .*2026$/)).toBeInTheDocument();
    expect(screen.queryByText(/^Ends /)).not.toBeInTheDocument();
  });

  it('a scheduled add-on end shows when it stops and the plan-only payment', async () => {
    servePlanScreens({
      subscription: proSubscription({
        secondRole: SecondRoleState.PaidAddOn,
        secondRoleAddOnEndsAt: PERIOD_END,
        priceLines: [planLine(59900), addOnLine(20000)],
      }),
    });

    renderCard();

    expect(await screen.findByText(/The second role stops being billed on .*2026\. New monthly payment: .*599/)).toBeInTheDocument();
  });

  it('a scheduled plan change replaces the add-on end line', async () => {
    servePlanScreens({
      subscription: proSubscription({
        secondRoleAddOnEndsAt: PERIOD_END,
        scheduledPlan: scheduled('start', 'Start', { priceLines: [planLine(29900)] }),
      }),
    });

    renderCard();

    expect(await screen.findByText(/Start begins on .*2026 · .*299/)).toBeInTheDocument();
    expect(screen.queryByText(/stops being billed/)).not.toBeInTheDocument();
  });
});

describe('PlanBillingCard second-role note', () => {
  it('a plan without a second role says so, without a date', async () => {
    servePlanScreens({ subscription: proSubscription({ secondRole: SecondRoleState.NotAvailable }) });

    renderCard();

    expect(await screen.findByText('A second professional role is not part of your plan. Change your plan to add one.')).toBeInTheDocument();
    expect(screen.queryByText(/after your plan change/)).not.toBeInTheDocument();
  });

  it('a scheduled plan change makes the note say when a second role can be added', async () => {
    servePlanScreens({
      subscription: proSubscription({
        secondRole: SecondRoleState.NotAvailable,
        scheduledPlan: scheduled('business', 'Business', { priceLines: [planLine(99900)] }),
      }),
    });

    renderCard();

    expect(await screen.findByText(/You can add a second role after your plan change on .*2026\./)).toBeInTheDocument();
    expect(screen.queryByText(/is not part of/)).not.toBeInTheDocument();
  });

  it('a plan that sells a second role shows no note on the card', async () => {
    servePlanScreens({ subscription: proSubscription({ secondRole: SecondRoleState.Purchasable }) });

    renderCard();

    expect(await screen.findByText(/Monthly · Renews .*2026/)).toBeInTheDocument();
    expect(screen.queryByText(/not part of/)).not.toBeInTheDocument();
    expect(screen.queryByText(/after your plan change/)).not.toBeInTheDocument();
  });
});

describe('PlanBillingCard cancelling a scheduled change', () => {
  const FREE_SCHEDULED = (cancellable: boolean) => proSubscription({ cancelAtPeriodEnd: true, scheduledPlan: scheduled('free', 'Free', { cancellable }) });

  it('confirming cancels the change, refetches and shows the toast', async () => {
    servePlanScreens({ subscription: FREE_SCHEDULED(true) });
    const cancelCalls: string[] = [];
    server.use(
      http.post(apiUrl('/billing/scheduled-change/cancel'), () => {
        cancelCalls.push('POST');
        // The refetch after the cancel no longer carries a scheduled change.
        servePlanScreens({ subscription: proSubscription() });
        return HttpResponse.json({ kind: 'SwitchToFree', planCode: 'free' });
      }),
    );

    const { user } = renderCard();
    expect(await screen.findByText(/^Free begins on .*2026$/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Cancel change' }));
    const dialog = await screen.findByRole('dialog', { name: 'Cancel the scheduled change?' });
    expect(within(dialog).getByText(/You stay on Pro and it renews on .*2026\. Nothing is charged now\./)).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Cancel change' }));

    await vi.waitFor(() => expect(toastMessages()).toContain('Change cancelled. You stay on Pro.'));
    await vi.waitFor(() => expect(screen.queryByText(/ begins on /)).not.toBeInTheDocument());
    expect(screen.queryByRole('button', { name: 'Cancel change' })).not.toBeInTheDocument();
    expect(cancelCalls).toEqual(['POST']);
  });

  it('the action is hidden when the change cannot be cancelled', async () => {
    servePlanScreens({ subscription: FREE_SCHEDULED(false) });

    renderCard();

    expect(await screen.findByText(/^Free begins on .*2026$/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Cancel change' })).not.toBeInTheDocument();
  });

  it('a provider error shows its message and keeps the scheduled line', async () => {
    servePlanScreens({ subscription: FREE_SCHEDULED(true) });
    server.use(http.post(apiUrl('/billing/scheduled-change/cancel'), () => rfc7807(502, 'BILLING_PROVIDER_ERROR')));

    const { user } = renderCard();
    await screen.findByText(/^Free begins on .*2026$/);
    await user.click(screen.getByRole('button', { name: 'Cancel change' }));
    await user.click(within(await screen.findByRole('dialog', { name: 'Cancel the scheduled change?' })).getByRole('button', { name: 'Cancel change' }));

    await vi.waitFor(() => expect(toastMessages()).toContain("The payment provider didn't answer. Please try again in a moment."));
    expect(await screen.findByText(/^Free begins on .*2026$/)).toBeInTheDocument();
  });
});

describe('PlanBillingCard Manage billing', () => {
  it('coming back to the app tab shows the subscription changed in the portal', async () => {
    const portalUrl = 'https://billing-portal.invalid/session/test';
    servePlanScreens({ subscription: proSubscription({ nameEn: 'Pro' }) });
    server.use(http.post(apiUrl('/billing/portal-session'), () => HttpResponse.json({ url: portalUrl })));
    // jsdom opens no tab: a stand-in the page can send to the portal.
    const replace = vi.fn();
    vi.stubGlobal('open', vi.fn(() => ({ opener: {}, closed: false, close: vi.fn(), location: { replace } })));

    const { user } = renderCard();
    expect(await screen.findByRole('heading', { name: 'Pro' })).toBeInTheDocument();
    await user.click(screen.getAllByRole('button', { name: 'Manage billing' })[0]);
    await vi.waitFor(() => expect(replace).toHaveBeenCalledWith(portalUrl));

    servePlanScreens({ subscription: proSubscription({ nameEn: 'Studio' }) });
    window.dispatchEvent(new Event('focus'));

    expect(await screen.findByRole('heading', { name: 'Studio' })).toBeInTheDocument();
  });
});
