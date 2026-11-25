/**
 * Return from Stripe Checkout, the one case that needs a real page reload: the watch for a late webhook lives in
 * sessionStorage and must survive it. The polling phases, the stop, other coaches' watches, failing reads and the
 * session refresh are proven by src/components/subscription/CheckoutActivationWatcher.test.tsx and
 * src/pages/BillingReturnPage.test.tsx. The subscription read is mocked with a mutable flag and the page clock is
 * faked, so the spec needs neither Stripe nor real waiting.
 */
import type { Page, Route } from '@playwright/test';
import { trainerTest, expect } from '../fixtures/auth';

const PERIOD_END = '2026-12-01T00:00:00Z';
const RETURN_WAIT_MS = 10_000;
const POLL_STEP_MS = 5_000;
const SLOW_STEP_MS = 30_000;
/** Mirrors the watch phases: 5 s polling to 2 min, 30 s polling to 15 min, then focus refresh only. */
const FAST_PHASE_MS = 120_000;
const WATCH_KEY = 'checkoutActivationWatch';

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
  return {
    setPaid: (value) => void (paid = value),
    reads: () => reads,
  };
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

type ToastCount = (text: string) => Promise<number>;

/**
 * Counts every toast title that was ever added to the page, whether or not it is still there. The faked clock
 * auto-closes a toast long before a spec finishes advancing, so "announced once" cannot be read off the DOM later.
 * The script runs again on every document, so after a reload the count starts from zero.
 */
async function trackToastTitles(page: Page): Promise<ToastCount> {
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

/** The watch the app keeps in sessionStorage, or null when none is stored. */
function storedWatch(page: Page): Promise<string | null> {
  return page.evaluate((key) => sessionStorage.getItem(key), WATCH_KEY);
}

/**
 * Lands on /billing/success with the plan unpaid and lets the return page give up, so the watch is running and
 * the pending note was shown. Ends about one second after the watch started.
 */
async function startWatch(page: Page): Promise<void> {
  await page.goto('/billing/success');
  await advance(page, PENDING_VISIBLE_ADVANCE_MS, PENDING_VISIBLE_STEP_MS);
  await expect(page).toHaveURL(/\/settings/);
  await expect(toastTitle(page, PENDING)).toBeVisible();
}

/** Moves the faked clock until the watch has left its 5 s phase and polls every 30 s. */
async function reachSlowPhase(page: Page): Promise<void> {
  await advance(page, FAST_PHASE_MS, SLOW_STEP_MS);
}

/**
 * Steps the clock in `step` sized ticks until the plan announcement has been shown. The toast closes after 5 s of the
 * faked clock, which a 30 s step outruns when the poll answer lands early in it, so the recorded toast titles are
 * read instead of the DOM.
 */
async function advanceUntilActivated(page: Page, step: number, toastsShown: ToastCount): Promise<void> {
  await expect(async () => {
    await advance(page, step, step);
    expect(await toastsShown(ACTIVATED)).toBeGreaterThan(0);
  }).toPass();
}

trainerTest.describe('checkout return with a late webhook', () => {
  // E2E: the watch lives in sessionStorage and must survive a real page reload.
  trainerTest('announces the plan once when it lands after a reload', async ({ page }) => {
    const mock = await mockSubscription(page, false);
    await page.clock.install();
    await startWatch(page);
    await reachSlowPhase(page);
    expect(await storedWatch(page)).not.toBeNull();

    // addInitScript only applies to documents loaded after it is registered, so register before the reload.
    // It runs again on the reloaded document, so the count starts from zero there.
    const toastsShown = await trackToastTitles(page);
    // No second clock.install(): the faked clock survives a reload, and installing again would reset it.
    await page.reload();
    await expect(page).toHaveURL(/\/settings/);
    await page.waitForLoadState('networkidle');
    await expect(toastTitle(page, ACTIVATED)).toHaveCount(0);

    mock.setPaid(true);
    await advanceUntilActivated(page, SLOW_STEP_MS, toastsShown);

    await page.waitForLoadState('networkidle');
    const readsAfterLanding = mock.reads();
    await advance(page, FAST_PHASE_MS / 2, SLOW_STEP_MS);
    expect(mock.reads()).toBe(readsAfterLanding);
    expect(await storedWatch(page)).toBeNull();
    // The recording script re-ran on the reloaded document, so this counts post-reload toasts only.
    expect(await toastsShown(ACTIVATED)).toBe(1);
  });
});
