/**
 * Settings "Plan and billing": Manage billing opens the Stripe portal in a new tab. Only what needs a real browser
 * is here (new tab, opener, blank-tab cleanup, pop-up fallback). The card's scheduled-change lines, second-role
 * note, cancel dialog and the refresh when the coach comes back are proven by
 * src/components/settings/PlanBillingCard.test.tsx. The subscription read is mocked so the spec does not depend on
 * Stripe or on the seeded plan catalogue.
 */
import type { Page, Route } from '@playwright/test';
import { trainerTest, expect } from '../fixtures/auth';

const PERIOD_END = '2026-12-01T00:00:00Z';

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

/** Toast text is also mirrored in a screen-reader live region, so assert on the toast title only. */
function toastTitle(page: Page, text: string | RegExp) {
  return page.locator('[data-slot="toast-title"]').filter({ hasText: text });
}

function subscription() {
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
    secondRole: 'NotAvailable',
    priceLines: [{ kind: 'Plan', amountMinorUnits: 59900, currency: 'CZK' }],
    features: {
      canCreatePlans: true,
      canMessage: true,
      canSendQuestionnaires: true,
      canUseWeeklyCheckIns: true,
      canUsePerClientCheckInConfig: false,
      canUsePayments: false,
    },
  };
}

async function mockSubscription(page: Page) {
  await page.route(/\/users\/me\/subscription$/, (route) => json(route, subscription()));
}

const PORTAL_URL = 'https://billing-portal.invalid/session/test';

/** The portal session answer plus a stub portal host, so no spec ever reaches real Stripe. */
async function mockPortal(page: Page, portalStatus: number) {
  await page.context().route(/\/billing\/portal-session$/, (route) =>
    portalStatus === 200
      ? json(route, { url: PORTAL_URL })
      : route.fulfill({
          status: portalStatus,
          contentType: 'application/problem+json',
          body: JSON.stringify({ status: portalStatus, title: 'Bad Gateway', errorCode: 'BILLING_PROVIDER_ERROR' }),
        }),
  );
  await page.context().route('https://billing-portal.invalid/**', (route) =>
    route.fulfill({ status: 200, contentType: 'text/html', body: '<!doctype html><title>Portal stub</title><p>Portal</p>' }),
  );
}

trainerTest.describe('settings plan card: Manage billing opens the portal in a new tab', () => {
  // E2E: a new tab and its opener only exist in a real browser.
  trainerTest('opens the portal in a new tab without an opener and keeps the app on Settings', async ({ page }) => {
    await mockSubscription(page);
    await mockPortal(page, 200);
    await page.goto('/settings#plan-billing');

    const popupPromise = page.waitForEvent('popup');
    await page.getByRole('button', { name: 'Manage billing' }).first().click();
    const popup = await popupPromise;

    await expect(popup).toHaveURL(PORTAL_URL);
    expect(await popup.evaluate(() => window.opener)).toBeNull();
    await expect(page).toHaveURL(/\/settings/);
  });

  // E2E: closing the blank tab the click opened needs a real browser's tab list.
  trainerTest('a failed portal session closes the blank tab and shows the error toast', async ({ page, context }) => {
    await mockSubscription(page);
    await mockPortal(page, 502);
    await page.goto('/settings#plan-billing');

    await page.getByRole('button', { name: 'Manage billing' }).first().click();

    await expect(toastTitle(page, "The payment provider didn't answer. Please try again in a moment.")).toBeVisible();
    await expect.poll(() => context.pages().length).toBe(1);
    await expect(page).toHaveURL(/\/settings/);
  });

  // E2E: the pop-up blocker fallback navigates the real tab.
  trainerTest('a blocked pop-up falls back to opening the portal in the same tab', async ({ page }) => {
    await page.addInitScript(() => {
      window.open = () => null;
    });
    await mockSubscription(page);
    await mockPortal(page, 200);
    await page.goto('/settings#plan-billing');

    await page.getByRole('button', { name: 'Manage billing' }).first().click();

    await page.waitForURL(PORTAL_URL);
  });
});
