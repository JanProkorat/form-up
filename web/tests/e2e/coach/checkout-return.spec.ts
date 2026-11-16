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

/** Advances the faked clock in steps (poll-sized by default), letting each answer arrive before the next tick. */
async function advance(page: Page, ms: number, step: number = POLL_STEP_MS): Promise<void> {
  for (let elapsed = 0; elapsed < ms; elapsed += step) {
    await page.clock.runFor(step);
    await page.waitForLoadState('networkidle');
  }
}

const PENDING_TEXT = 'Your payment went through';
const PENDING = new RegExp(PENDING_TEXT);
const ACTIVATED = 'Your Pro plan is now active.';

/**
 * A toast's text is also rendered in a screen-reader live region, so assertions target the toast title only.
 * Toasts close after 5 s of the faked clock, so to see the pending note the clock may only run 1 s past the
 * return page's wait; a longer advance outlives it.
 */
function toastTitle(page: Page, text: string | RegExp) {
  return page.locator('[data-slot="toast-title"]').filter({ hasText: text });
}
const PENDING_VISIBLE_ADVANCE_MS = RETURN_WAIT_MS + 1_000;
const PENDING_VISIBLE_STEP_MS = 1_000;

/**
 * Counts every toast title that was ever added to the page, whether or not it is still there. The faked clock
 * auto-closes a toast long before a spec finishes advancing, so "announced once" cannot be read off the DOM later.
 */
async function trackToastTitles(page: Page): Promise<(text: string) => Promise<number>> {
  await page.addInitScript(() => {
    const seen = new Set<Element>();
    const texts: string[] = [];
    const scan = () => {
      document.querySelectorAll('[data-slot="toast-title"]').forEach((element) => {
        if (seen.has(element)) return;
        seen.add(element);
        texts.push(element.textContent ?? '');
      });
    };
    new MutationObserver(scan).observe(document, { childList: true, subtree: true, characterData: true });
    (window as unknown as { __toastTitles: () => string[] }).__toastTitles = () => texts;
  });
  return async (text) => {
    const titles = await page.evaluate(() => (window as unknown as { __toastTitles: () => string[] }).__toastTitles());
    return titles.filter((title) => title.includes(text)).length;
  };
}

trainerTest.describe('checkout return with a late webhook', () => {
  trainerTest('announces the plan when it lands after the return page gave up', async ({ page }) => {
    const mock = await mockSubscription(page, false);
    const toastsShown = await trackToastTitles(page);
    await page.clock.install();
    await page.goto('/billing/success');

    await advance(page, PENDING_VISIBLE_ADVANCE_MS, PENDING_VISIBLE_STEP_MS);
    await expect(page).toHaveURL(/\/settings/);
    await expect(toastTitle(page, PENDING)).toBeVisible();
    await expect(toastTitle(page, ACTIVATED)).toHaveCount(0);

    mock.setPaid(true);
    await expect(async () => {
      await advance(page, POLL_STEP_MS);
      await expect(toastTitle(page, ACTIVATED)).toBeVisible({ timeout: 500 });
    }).toPass();

    // The watch ends with the announcement: no further reads on the polling cadence.
    await page.waitForLoadState('networkidle');
    const readsAfterLanding = mock.reads();
    await advance(page, WATCH_WINDOW_MS / 2);
    expect(mock.reads()).toBe(readsAfterLanding);
    // The toast has closed by now (5 s of the faked clock), so count the ones that were ever shown.
    expect(await toastsShown(ACTIVATED)).toBe(1);
  });

  trainerTest('stops polling after the watch window when the plan never lands', async ({ page }) => {
    const mock = await mockSubscription(page, false);
    const toastsShown = await trackToastTitles(page);
    await page.clock.install();
    await page.goto('/billing/success');

    await advance(page, PENDING_VISIBLE_ADVANCE_MS, PENDING_VISIBLE_STEP_MS);
    await expect(toastTitle(page, PENDING)).toBeVisible();

    await advance(page, WATCH_WINDOW_MS + POLL_STEP_MS);
    const readsAtBound = mock.reads();
    await advance(page, WATCH_WINDOW_MS / 2);

    expect(mock.reads()).toBe(readsAtBound);
    await expect(toastTitle(page, ACTIVATED)).toHaveCount(0);
    // The note closed on its own long ago; it was announced once and not again by the watch.
    expect(await toastsShown(PENDING_TEXT)).toBe(1);
  });

  trainerTest('a plan that is already paid continues without a note', async ({ page }) => {
    await mockSubscription(page, true);
    await page.clock.install();
    await page.goto('/billing/success');

    await expect(page).toHaveURL(/\/settings/);
    await expect(toastTitle(page, PENDING)).toHaveCount(0);
    await expect(toastTitle(page, ACTIVATED)).toHaveCount(0);
  });
});
