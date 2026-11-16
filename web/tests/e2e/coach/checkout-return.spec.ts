/**
 * Return from Stripe Checkout: when the webhook lands after the 10 s wait on /billing/success, the app keeps
 * refreshing the subscription in the background and announces the plan once it arrives. The subscription read is
 * mocked with a mutable flag and the page clock is faked, so the spec needs neither Stripe nor real waiting.
 */
import type { Page, Route } from '@playwright/test';
import { trainerTest, expect } from '../fixtures/auth';

const PERIOD_END = '2026-12-01T00:00:00Z';
const RETURN_WAIT_MS = 10_000;
const POLL_STEP_MS = 5_000;
const WATCH_WINDOW_MS = 120_000;

const FEATURES = {
  canCreatePlans: true,
  canMessage: true,
  canSendQuestionnaires: true,
  canUseWeeklyCheckIns: true,
  canUsePerClientCheckInConfig: false,
  canUsePayments: false,
};

function subscription(paid: boolean) {
  return {
    planCode: paid ? 'pro' : 'free',
    nameCs: paid ? 'Pro' : 'Zdarma',
    nameEn: paid ? 'Pro' : 'Free',
    nameDe: paid ? 'Pro' : 'Kostenlos',
    status: 'Active',
    taxMode: 'None',
    hasBillingAccount: paid,
    cancelAtPeriodEnd: false,
    currentPeriodEndsAt: paid ? PERIOD_END : null,
    isSetByFormUp: false,
    isLapsed: false,
    planChoiceRequired: false,
    keepClientsChoiceRequired: false,
    overCapClientCount: 0,
    freeSlotCount: 0,
    activeClientCount: 4,
    clientLimit: paid ? 30 : 3,
    secondRole: 'NotAvailable',
    priceLines: paid ? [{ kind: 'Plan', amountMinorUnits: 59900, currency: 'CZK' }] : [],
    features: FEATURES,
  };
}

interface SubscriptionMock {
  setPaid: (paid: boolean) => void;
  reads: () => number;
}

/** Answers the subscription read from a flag the test flips, and counts the reads. */
async function mockSubscription(page: Page, initiallyPaid: boolean): Promise<SubscriptionMock> {
  let paid = initiallyPaid;
  let reads = 0;
  await page.route(/\/users\/me\/subscription$/, (route: Route) => {
    reads += 1;
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(subscription(paid)) });
  });
  return { setPaid: (value) => void (paid = value), reads: () => reads };
}

/** Advances the faked clock in poll-sized steps, letting each answer arrive before the next tick. */
async function advance(page: Page, ms: number): Promise<void> {
  for (let elapsed = 0; elapsed < ms; elapsed += POLL_STEP_MS) {
    await page.clock.runFor(POLL_STEP_MS);
    await page.waitForLoadState('networkidle');
  }
}

const PENDING = /Your payment went through/;
const ACTIVATED = 'Your Pro plan is now active.';

trainerTest.describe('checkout return with a late webhook', () => {
  trainerTest('announces the plan when it lands after the return page gave up', async ({ page }) => {
    const mock = await mockSubscription(page, false);
    await page.clock.install();
    await page.goto('/billing/success');

    await advance(page, RETURN_WAIT_MS + POLL_STEP_MS);
    await expect(page).toHaveURL(/\/settings/);
    await expect(page.getByText(PENDING)).toBeVisible();
    await expect(page.getByText(ACTIVATED)).toHaveCount(0);

    mock.setPaid(true);
    await expect(async () => {
      await page.clock.runFor(POLL_STEP_MS);
      await expect(page.getByText(ACTIVATED)).toBeVisible({ timeout: 500 });
    }).toPass();

    // The watch ends with the announcement: no further reads on the polling cadence.
    await page.waitForLoadState('networkidle');
    const readsAfterLanding = mock.reads();
    await advance(page, WATCH_WINDOW_MS / 2);
    expect(mock.reads()).toBe(readsAfterLanding);
    await expect(page.getByText(ACTIVATED)).toHaveCount(1);
  });

  trainerTest('stops polling after the watch window when the plan never lands', async ({ page }) => {
    const mock = await mockSubscription(page, false);
    await page.clock.install();
    await page.goto('/billing/success');

    await advance(page, RETURN_WAIT_MS + POLL_STEP_MS);
    await expect(page.getByText(PENDING)).toBeVisible();

    await advance(page, WATCH_WINDOW_MS + POLL_STEP_MS);
    const readsAtBound = mock.reads();
    await advance(page, WATCH_WINDOW_MS / 2);

    expect(mock.reads()).toBe(readsAtBound);
    await expect(page.getByText(ACTIVATED)).toHaveCount(0);
    await expect(page.getByText(PENDING)).toBeVisible();
  });

  trainerTest('a plan that is already paid continues without a note', async ({ page }) => {
    await mockSubscription(page, true);
    await page.clock.install();
    await page.goto('/billing/success');

    await expect(page).toHaveURL(/\/settings/);
    await expect(page.getByText(PENDING)).toHaveCount(0);
    await expect(page.getByText(ACTIVATED)).toHaveCount(0);
  });
});
