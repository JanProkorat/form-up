/**
 * Settings > Plan and billing — the card follows the billing interval the subscription reports: the renewal line,
 * the bill title and totals, the next-year amount, and the scheduled-change and add-role texts.
 */
import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import {
  BillingInterval,
  PromotionBillingPeriod,
  PromotionDuration,
  PromotionKind,
  SubscriptionPriceLineKind,
} from '@/api/generated';
import PlanBillingCard from '@/components/settings/PlanBillingCard';
import { PERIOD_END, liveSubscription, renderCoachScreen, servePlanScreens } from '@/test/planPicker';

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
