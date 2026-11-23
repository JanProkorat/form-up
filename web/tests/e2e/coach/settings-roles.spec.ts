/**
 * Settings "Roles": what Add role does for a one-role coach depends on the plan's second-role state.
 * Purchasable opens the paid dialog, Included the free one, NotAvailable nothing but a note. The subscription,
 * roles and preview reads are mocked so the spec does not depend on Stripe or the seeded plan catalogue.
 */
import type { Page, Route } from '@playwright/test';
import { trainerTest, expect } from '../fixtures/auth';

const PERIOD_END = '2026-12-01T00:00:00Z';

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

function subscription(extra: Record<string, unknown>) {
  return {
    planCode: 'pro',
    nameCs: 'Pro',
    nameEn: 'Pro',
    nameDe: 'Pro',
    status: 'Active',
    taxMode: 'None',
    hasBillingAccount: true,
    cancelAtPeriodEnd: false,
    currentPeriodEndsAt: PERIOD_END,
    isSetByFormUp: false,
    isLapsed: false,
    planChoiceRequired: false,
    keepClientsChoiceRequired: false,
    overCapClientCount: 0,
    freeSlotCount: 0,
    activeClientCount: 4,
    clientLimit: 30,
    secondRole: 'Purchasable',
    priceLines: [{ kind: 'Plan', amountMinorUnits: 59900, currency: 'CZK' }],
    features: {
      canCreatePlans: true,
      canMessage: true,
      canSendQuestionnaires: true,
      canUseWeeklyCheckIns: true,
      canUsePerClientCheckInConfig: false,
      canUsePayments: false,
    },
    ...extra,
  };
}

/** Mocks the three reads the card makes and records any POST /users/me/roles so a test can assert none was sent. */
async function mockRolesCard(
  page: Page,
  body: Record<string, unknown>,
  roles: { role: string; clientCount: number; sharedWithOtherRoleCount: number }[] = [
    { role: 'Trainer', clientCount: 4, sharedWithOtherRoleCount: 0 },
  ],
) {
  const addRoleRequests: string[] = [];
  await page.route(/\/users\/me\/subscription$/, (route) => json(route, body));
  await page.route(/\/users\/me\/roles$/, (route) => {
    if (route.request().method() === 'POST') {
      addRoleRequests.push(route.request().url());
      return route.fulfill({ status: 403, contentType: 'application/json', body: '{}' });
    }
    return json(route, { roles });
  });
  await page.route(/\/billing\/second-role\/preview$/, (route) =>
    json(route, {
      planAmountMinorUnits: 59900,
      addOnAmountMinorUnits: 20000,
      newTotalMinorUnits: 79900,
      billingInterval: 'Monthly',
      currency: 'CZK',
      todayAmountMinorUnits: 10000,
      todayTaxMinorUnits: 0,
      periodEndsAt: PERIOD_END,
    }),
  );
  return addRoleRequests;
}

trainerTest.describe('settings roles card, adding a second role', () => {
  trainerTest('a purchasable second role opens the paid dialog and adds nothing yet', async ({ page }) => {
    const addRoleRequests = await mockRolesCard(page, subscription({ secondRole: 'Purchasable' }));
    await page.goto('/settings');

    await page.getByRole('button', { name: 'Add role' }).click();

    await expect(page.getByRole('dialog').getByText('Add the Nutritionist role?')).toBeVisible();
    await expect(page.getByRole('button', { name: /^Add role · pay / })).toBeEnabled();
    expect(addRoleRequests).toHaveLength(0);
  });

  trainerTest('a payment needing bank confirmation shows the link, adds no role and starts the watch', async ({
    page,
  }) => {
    const addRoleRequests = await mockRolesCard(page, subscription({ secondRole: 'Purchasable' }));
    const paymentUrl = 'https://stripe-stub.example.test/i/acct_test/invoice_test';
    await page.route(/\/billing\/second-role\/confirm$/, (route) =>
      json(route, { paysSecondRoleAddOn: false, paymentUrl }),
    );
    // The hosted page opens in a new tab; keep the browser off the real host.
    await page.context().route(/^https:\/\/stripe-stub\.example\.test\//, (route) =>
      route.fulfill({ status: 200, contentType: 'text/html', body: '<html><body>stub</body></html>' }),
    );
    await page.goto('/settings');

    await page.getByRole('button', { name: 'Add role' }).click();
    await page.getByRole('button', { name: /^Add role · pay / }).click();

    const notice = page.getByTestId('upgrade-payment-notice');
    await expect(notice).toBeVisible();
    await expect(notice.getByRole('link')).toHaveCount(0);
    const link = page.getByRole('dialog').getByRole('link', { name: 'Continue to payment' });
    await expect(link).toHaveAttribute('href', paymentUrl);
    await expect(link).toHaveAttribute('target', '_blank');
    expect(addRoleRequests).toHaveLength(0);

    const popupPromise = page.waitForEvent('popup');
    await link.click();
    const popup = await popupPromise;
    await popup.close();

    await expect
      .poll(() => page.evaluate(() => sessionStorage.getItem('checkoutActivationWatch')))
      .toContain('"awaitsSecondRoleAddOn":true');
    const stored = await page.evaluate(() => sessionStorage.getItem('checkoutActivationWatch'));
    expect(stored).not.toContain('targetPlanCode');
    expect(addRoleRequests).toHaveLength(0);
  });

  trainerTest('an unavailable second role disables Add and says the plan lacks it', async ({ page }) => {
    const addRoleRequests = await mockRolesCard(page, subscription({ secondRole: 'NotAvailable' }));
    await page.goto('/settings');

    await expect(page.locator('#roles').getByText(/A second professional role is not part of your plan/)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Add role' })).toBeDisabled();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(addRoleRequests).toHaveLength(0);
  });

  trainerTest('an unavailable second role with a scheduled plan change names the date', async ({ page }) => {
    const addRoleRequests = await mockRolesCard(
      page,
      subscription({
        secondRole: 'NotAvailable',
        scheduledPlan: {
          planCode: 'start',
          nameCs: 'Start',
          nameEn: 'Start',
          nameDe: 'Start',
          startsAt: PERIOD_END,
          priceLines: [{ kind: 'Plan', amountMinorUnits: 29900, currency: 'CZK' }],
        },
      }),
    );
    await page.goto('/settings');

    await expect(page.locator('#roles').getByText(/You can add a second role after your plan change on .*2026\./)).toBeVisible();
    await expect(page.getByText(/is not part of your plan/)).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Add role' })).toBeDisabled();
    expect(addRoleRequests).toHaveLength(0);
  });

  trainerTest('an included second role opens the free dialog', async ({ page }) => {
    await mockRolesCard(page, subscription({ secondRole: 'Included' }));
    await page.goto('/settings');

    await page.getByRole('button', { name: 'Add role' }).click();

    const dialog = page.getByRole('dialog');
    await expect(dialog.getByText('Add the Nutritionist role?')).toBeVisible();
    await expect(dialog.getByText(/You get the tools for this role/)).toBeVisible();
    await expect(dialog.getByRole('button', { name: /pay / })).toHaveCount(0);
  });
});

const TWO_ROLES = [
  { role: 'Trainer', clientCount: 4, sharedWithOtherRoleCount: 0 },
  { role: 'Nutritionist', clientCount: 2, sharedWithOtherRoleCount: 0 },
];

trainerTest.describe('settings roles card, removing a second role', () => {
  trainerTest('a billed add-on shows when it stops and the plan-only payment', async ({ page }) => {
    await mockRolesCard(
      page,
      subscription({
        secondRole: 'PaidAddOn',
        priceLines: [
          { kind: 'Plan', amountMinorUnits: 59900, currency: 'CZK' },
          { kind: 'SecondRoleAddOn', amountMinorUnits: 20000, currency: 'CZK' },
        ],
      }),
      TWO_ROLES,
    );
    await page.goto('/settings');

    await page.getByRole('button', { name: 'Remove role' }).first().click();

    await expect(
      page.getByRole('dialog').getByText(/The second role stops being billed on .*2026\. New monthly payment: .*599/),
    ).toBeVisible();
  });

  trainerTest('an included second role shows no billing line', async ({ page }) => {
    await mockRolesCard(page, subscription({ secondRole: 'Included' }), TWO_ROLES);
    await page.goto('/settings');

    await page.getByRole('button', { name: 'Remove role' }).first().click();

    await expect(page.getByRole('dialog')).toBeVisible();
    await expect(page.getByRole('dialog').getByText(/stops being billed/)).toHaveCount(0);
  });

  trainerTest('a billing provider failure keeps the dialog open with the message', async ({ page }) => {
    await mockRolesCard(page, subscription({ secondRole: 'Included' }), TWO_ROLES);
    await page.route(/\/users\/me\/roles\/Nutritionist$/, (route) =>
      route.fulfill({
        status: 502,
        contentType: 'application/problem+json',
        body: JSON.stringify({ status: 502, errorCode: 'BILLING_PROVIDER_ERROR' }),
      }),
    );
    await page.goto('/settings');

    await page.getByRole('button', { name: 'Remove role' }).nth(1).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Remove Nutritionist' }).click();

    await expect(page.getByRole('dialog').getByRole('alert')).toHaveText(/payment provider didn't answer/);
  });
});
