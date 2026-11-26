/**
 * Settings > Roles — what Add role does for a one-role coach depends on the plan's second-role state: Purchasable
 * opens the paid dialog, Included the free one, NotAvailable nothing but a note. Removing a second role names the
 * billing change and keeps the dialog open on a billing failure.
 */
import { afterEach, describe, expect, it } from 'vitest';
import { screen, within } from '@testing-library/react';
import { BillingInterval, SecondRoleState, SubscriptionPriceLineKind } from '@/api/generated';
import type { GetMyCoachRolesResponse, GetMySubscriptionResponse, PreviewSecondRoleAddOnResponse } from '@/api/generated';
import RolesCard from '@/components/settings/RolesCard';
import { useCheckoutActivationStore } from '@/stores/checkoutActivation';
import { PERIOD_END, liveSubscription, renderCoachScreen, servePlanScreens } from '@/test/planPicker';
import { HttpResponse, apiUrl, http, rfc7807 } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

const ONE_ROLE: GetMyCoachRolesResponse = { roles: [{ role: 'Trainer', clientCount: 4, sharedWithOtherRoleCount: 0 }] };
const TWO_ROLES: GetMyCoachRolesResponse = {
  roles: [
    { role: 'Trainer', clientCount: 4, sharedWithOtherRoleCount: 0 },
    { role: 'Nutritionist', clientCount: 2, sharedWithOtherRoleCount: 0 },
  ],
};

const planLine = (amount: number) => ({ kind: SubscriptionPriceLineKind.Plan, amountMinorUnits: amount, currency: 'CZK' });
const addOnLine = (amount: number) => ({ kind: SubscriptionPriceLineKind.SecondRoleAddOn, amountMinorUnits: amount, currency: 'CZK' });

const subscription = (overrides: Partial<GetMySubscriptionResponse>) =>
  liveSubscription('pro', BillingInterval.Monthly, { priceLines: [planLine(59900)], ...overrides });

const PREVIEW: PreviewSecondRoleAddOnResponse = {
  planAmountMinorUnits: 59900,
  addOnAmountMinorUnits: 20000,
  newTotalMinorUnits: 79900,
  billingInterval: BillingInterval.Monthly,
  currency: 'CZK',
  todayAmountMinorUnits: 10000,
  todayTaxMinorUnits: 0,
  periodEndsAt: PERIOD_END,
};

/** Serves the card's reads, the add-on preview, and records any POST /users/me/roles (a refusal: none must be sent). */
function serveCard(sub: GetMySubscriptionResponse, roles: GetMyCoachRolesResponse = ONE_ROLE) {
  const addRoleRequests: string[] = [];
  servePlanScreens({ subscription: sub, roles });
  server.use(
    http.post(apiUrl('/billing/second-role/preview'), () => HttpResponse.json(PREVIEW)),
    http.post(apiUrl('/users/me/roles'), () => {
      addRoleRequests.push('POST');
      return HttpResponse.json({}, { status: 403 });
    }),
  );
  return addRoleRequests;
}

const renderCard = () => renderCoachScreen(<RolesCard />, '/settings');
const addRole = () => screen.findByRole('button', { name: 'Add role' });

afterEach(() => {
  useCheckoutActivationStore.getState().stop();
});

describe('RolesCard adding a second role', () => {
  it('a purchasable second role opens the paid dialog and adds nothing yet', async () => {
    const addRoleRequests = serveCard(subscription({ secondRole: SecondRoleState.Purchasable }));

    const { user } = renderCard();
    await user.click(await addRole());

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('Add the Nutritionist role?')).toBeInTheDocument();
    expect(await within(dialog).findByRole('button', { name: /^Add role · pay / })).toBeEnabled();
    expect(addRoleRequests).toHaveLength(0);
  });

  it('a payment needing bank confirmation shows the link, adds no role and starts the watch', async () => {
    const paymentUrl = 'https://stripe-stub.example.test/i/acct_test/invoice_test';
    const addRoleRequests = serveCard(subscription({ secondRole: SecondRoleState.Purchasable }));
    server.use(http.post(apiUrl('/billing/second-role/confirm'), () => HttpResponse.json({ paysSecondRoleAddOn: false, paymentUrl })));

    const { user } = renderCard();
    await user.click(await addRole());
    const dialog = await screen.findByRole('dialog');
    await user.click(await within(dialog).findByRole('button', { name: /^Add role · pay / }));

    const notice = await screen.findByTestId('upgrade-payment-notice');
    expect(within(notice).queryByRole('link')).not.toBeInTheDocument();
    const link = within(dialog).getByRole('link', { name: 'Continue to payment' });
    expect(link).toHaveAttribute('href', paymentUrl);
    expect(link).toHaveAttribute('target', '_blank');
    expect(addRoleRequests).toHaveLength(0);

    // jsdom opens no tab: the click is what starts the watch for the add-on.
    await user.click(link);

    const stored = sessionStorage.getItem('checkoutActivationWatch');
    expect(stored).toContain('"awaitsSecondRoleAddOn":true');
    expect(stored).not.toContain('targetPlanCode');
    expect(addRoleRequests).toHaveLength(0);
  });

  it('an unavailable second role disables Add and says the plan lacks it', async () => {
    const addRoleRequests = serveCard(subscription({ secondRole: SecondRoleState.NotAvailable }));

    renderCard();

    expect(await screen.findByText(/A second professional role is not part of your plan/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Add role' })).toBeDisabled();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(addRoleRequests).toHaveLength(0);
  });

  it('an unavailable second role with a scheduled plan change names the date', async () => {
    const addRoleRequests = serveCard(
      subscription({
        secondRole: SecondRoleState.NotAvailable,
        scheduledPlan: { planCode: 'start', nameEn: 'Start', startsAt: PERIOD_END, priceLines: [planLine(29900)] },
      }),
    );

    renderCard();

    expect(await screen.findByText(/You can add a second role after your plan change on .*2026\./)).toBeInTheDocument();
    expect(screen.queryByText(/is not part of your plan/)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Add role' })).toBeDisabled();
    expect(addRoleRequests).toHaveLength(0);
  });

  it('an included second role opens the free dialog', async () => {
    serveCard(subscription({ secondRole: SecondRoleState.Included }));

    const { user } = renderCard();
    await user.click(await addRole());

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('Add the Nutritionist role?')).toBeInTheDocument();
    expect(within(dialog).getByText(/You get the tools for this role/)).toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: /pay / })).not.toBeInTheDocument();
  });
});

describe('RolesCard removing a second role', () => {
  const removeButtons = () => screen.findAllByRole('button', { name: 'Remove role' });

  it('a billed add-on shows when it stops and the plan-only payment', async () => {
    serveCard(subscription({ secondRole: SecondRoleState.PaidAddOn, priceLines: [planLine(59900), addOnLine(20000)] }), TWO_ROLES);

    const { user } = renderCard();
    await user.click((await removeButtons())[0]);

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/The second role stops being billed on .*2026\. New monthly payment: .*599/)).toBeInTheDocument();
  });

  it('an included second role shows no billing line', async () => {
    serveCard(subscription({ secondRole: SecondRoleState.Included }), TWO_ROLES);

    const { user } = renderCard();
    await user.click((await removeButtons())[0]);

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).queryByText(/stops being billed/)).not.toBeInTheDocument();
  });

  it('a billing provider failure keeps the dialog open with the message', async () => {
    serveCard(subscription({ secondRole: SecondRoleState.Included }), TWO_ROLES);
    server.use(http.delete(apiUrl('/users/me/roles/Nutritionist'), () => rfc7807(502, 'BILLING_PROVIDER_ERROR')));

    const { user } = renderCard();
    await user.click((await removeButtons())[1]);
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Remove Nutritionist' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(/payment provider didn't answer/);
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });
});
