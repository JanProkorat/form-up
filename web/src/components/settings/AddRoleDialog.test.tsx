/**
 * Add-role dialog — the new bill follows the interval of the second-role preview: "/ year" on an annual subscription,
 * "/ month" on a monthly one, including the date a discount ends.
 */
import { describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import { BillingInterval, BillingTaxMode } from '@/api/generated';
import type { PreviewSecondRoleAddOnResponse } from '@/api/generated';
import AddRoleDialog from '@/components/settings/AddRoleDialog';
import { ANNUAL_PLANS, PERIOD_END, liveSubscription, renderCoachScreen, servePlanScreens } from '@/test/planPicker';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

function servePreview(overrides: Partial<PreviewSecondRoleAddOnResponse>) {
  const body: PreviewSecondRoleAddOnResponse = {
    planAmountMinorUnits: 59900,
    addOnAmountMinorUnits: 29900,
    newTotalMinorUnits: 89800,
    billingInterval: BillingInterval.Monthly,
    currency: 'CZK',
    todayAmountMinorUnits: 12000,
    todayTaxMinorUnits: 0,
    periodEndsAt: PERIOD_END,
    taxMode: BillingTaxMode.None,
    ...overrides,
  };
  server.use(http.post(apiUrl('/billing/second-role/preview'), () => HttpResponse.json(body)));
}

const renderDialog = () =>
  renderCoachScreen(<AddRoleDialog role="nutritionist" onClose={vi.fn()} onPaid={vi.fn()} adding={false} />, '/settings');

/** The plan list with Studio also sold yearly (the shared fixture sells it monthly only). */
const PLANS_STUDIO_YEARLY = ANNUAL_PLANS.map((plan) =>
  plan.code === 'studio' ? { ...plan, annualPriceMinorUnits: 999000 } : plan,
);

describe('AddRoleDialog billing interval', () => {
  it('an annual subscription shows the new total per year', async () => {
    servePlanScreens({ subscription: liveSubscription('pro', BillingInterval.Annual), plans: PLANS_STUDIO_YEARLY });
    servePreview({
      planAmountMinorUnits: 599000,
      addOnAmountMinorUnits: 299000,
      newTotalMinorUnits: 898000,
      billingInterval: BillingInterval.Annual,
    });

    renderDialog();

    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText(/8,980.* \/ year/)).toBeInTheDocument();
    expect(within(dialog).queryByText(/\/ month/)).not.toBeInTheDocument();
  });

  it('a discount end date names the full yearly price', async () => {
    servePlanScreens({ subscription: liveSubscription('pro', BillingInterval.Annual) });
    servePreview({
      planAmountMinorUnits: 599000,
      addOnAmountMinorUnits: 299000,
      newTotalMinorUnits: 898000,
      discountedPlanAmountMinorUnits: 419300,
      discountedAddOnAmountMinorUnits: 209300,
      discountedNewTotalMinorUnits: 628600,
      promotionEndsAt: PERIOD_END,
      billingInterval: BillingInterval.Annual,
    });

    renderDialog();

    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText(/6,286.* \/ year/)).toBeInTheDocument();
    expect(within(dialog).getByText(/Your discount runs until .*2026, then .*8,980.* \/ year\./)).toBeInTheDocument();
  });

  it('an annual subscriber sees the Studio line at its yearly price', async () => {
    servePlanScreens({ subscription: liveSubscription('pro', BillingInterval.Annual), plans: PLANS_STUDIO_YEARLY });
    servePreview({
      planAmountMinorUnits: 599000,
      addOnAmountMinorUnits: 299000,
      newTotalMinorUnits: 898000,
      billingInterval: BillingInterval.Annual,
    });

    renderDialog();

    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText(/Studio includes both roles for .*9,990.* \/ year\./)).toBeInTheDocument();
    expect(within(dialog).queryByText(/Studio includes both roles for .*\/ month/)).not.toBeInTheDocument();
  });

  it('an annual subscriber falls back to the monthly Studio price when Studio is not sold yearly', async () => {
    servePlanScreens({ subscription: liveSubscription('pro', BillingInterval.Annual) });
    servePreview({
      planAmountMinorUnits: 599000,
      addOnAmountMinorUnits: 299000,
      newTotalMinorUnits: 898000,
      billingInterval: BillingInterval.Annual,
    });

    renderDialog();

    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText(/Studio includes both roles for .*999.* \/ month\./)).toBeInTheDocument();
  });

  it('a monthly subscriber sees the Studio line at its monthly price', async () => {
    servePlanScreens({ subscription: liveSubscription('pro', BillingInterval.Monthly) });
    servePreview({});

    renderDialog();

    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText(/Studio includes both roles for .*999.* \/ month\./)).toBeInTheDocument();
  });

  it('a monthly subscription keeps the monthly total', async () => {
    servePlanScreens({ subscription: liveSubscription('pro', BillingInterval.Monthly) });
    servePreview({});

    renderDialog();

    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText(/898.* \/ month/)).toBeInTheDocument();
    expect(within(dialog).queryByText(/\/ year/)).not.toBeInTheDocument();
  });
});
