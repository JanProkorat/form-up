/**
 * Settings "Plan and billing": a scheduled plan change replaces the "Ends <date>" line with "<current> until <date>"
 * and an accent line with the plan that begins next. The subscription read is mocked so the spec does not depend on
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
    ...extra,
  };
}

async function mockSubscription(page: Page, body: Record<string, unknown>) {
  await page.route(/\/users\/me\/subscription$/, (route) => json(route, body));
}

trainerTest.describe('settings plan card with a scheduled change', () => {
  trainerTest('a scheduled downgrade shows the current plan until the date and the next plan with its price', async ({ page }) => {
    await mockSubscription(
      page,
      subscription({
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
    await page.goto('/settings#plan-billing');

    await expect(page.getByText(/Monthly · Pro until .*2026/)).toBeVisible();
    await expect(page.getByText(/Start begins on .*2026 · .*299.* \/ month/)).toBeVisible();
    await expect(page.getByText(/^Ends /)).toHaveCount(0);
  });

  trainerTest('a scheduled switch to Free shows when Free begins, without a price', async ({ page }) => {
    await mockSubscription(
      page,
      subscription({
        cancelAtPeriodEnd: true,
        scheduledPlan: {
          planCode: 'free',
          nameCs: 'Free',
          nameEn: 'Free',
          nameDe: 'Free',
          startsAt: PERIOD_END,
          priceLines: [],
        },
      }),
    );
    await page.goto('/settings#plan-billing');

    await expect(page.getByText(/Monthly · Pro until .*2026/)).toBeVisible();
    await expect(page.getByText(/^Free begins on .*2026$/)).toBeVisible();
    await expect(page.getByText(/^Ends /)).toHaveCount(0);
  });

  trainerTest('a scheduled add-on end shows when it stops and the plan-only payment', async ({ page }) => {
    await mockSubscription(
      page,
      subscription({
        secondRole: 'PaidAddOn',
        secondRoleAddOnEndsAt: PERIOD_END,
        priceLines: [
          { kind: 'Plan', amountMinorUnits: 59900, currency: 'CZK' },
          { kind: 'SecondRoleAddOn', amountMinorUnits: 20000, currency: 'CZK' },
        ],
      }),
    );
    await page.goto('/settings#plan-billing');

    await expect(page.getByText(/The second role stops being billed on .*2026\. New monthly payment: .*599/)).toBeVisible();
  });

  trainerTest('a scheduled plan change replaces the add-on end line', async ({ page }) => {
    await mockSubscription(
      page,
      subscription({
        secondRoleAddOnEndsAt: PERIOD_END,
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
    await page.goto('/settings#plan-billing');

    await expect(page.getByText(/Start begins on .*2026 · .*299/)).toBeVisible();
    await expect(page.getByText(/stops being billed/)).toHaveCount(0);
  });

  trainerTest('without a scheduled change the card keeps its renewal line', async ({ page }) => {
    await mockSubscription(page, subscription({}));
    await page.goto('/settings#plan-billing');

    await expect(page.getByText(/Monthly · Renews .*2026/)).toBeVisible();
    await expect(page.getByText(/ begins on /)).toHaveCount(0);
  });
});

trainerTest.describe('settings plan card second-role note', () => {
  // RolesCard shows the same wording, so every locator is scoped to the plan card.
  trainerTest('a plan without a second role says so, without a date', async ({ page }) => {
    await mockSubscription(page, subscription({ secondRole: 'NotAvailable' }));
    await page.goto('/settings#plan-billing');

    const card = page.locator('#plan-billing');
    await expect(card.getByText('A second professional role is not part of your plan. Change your plan to add one.')).toBeVisible();
    await expect(card.getByText(/after your plan change/)).toHaveCount(0);
  });

  trainerTest('a scheduled plan change makes the note say when a second role can be added', async ({ page }) => {
    await mockSubscription(
      page,
      subscription({
        secondRole: 'NotAvailable',
        scheduledPlan: {
          planCode: 'business',
          nameCs: 'Business',
          nameEn: 'Business',
          nameDe: 'Business',
          startsAt: PERIOD_END,
          priceLines: [{ kind: 'Plan', amountMinorUnits: 99900, currency: 'CZK' }],
        },
      }),
    );
    await page.goto('/settings#plan-billing');

    const card = page.locator('#plan-billing');
    await expect(card.getByText(/You can add a second role after your plan change on .*2026\./)).toBeVisible();
    await expect(card.getByText(/is not part of/)).toHaveCount(0);
  });

  trainerTest('a plan that sells a second role shows no note on the card', async ({ page }) => {
    await mockSubscription(page, subscription({ secondRole: 'Purchasable' }));
    await page.goto('/settings#plan-billing');

    const card = page.locator('#plan-billing');
    await expect(card.getByText(/Monthly · Renews .*2026/)).toBeVisible();
    await expect(card.getByText(/not part of/)).toHaveCount(0);
    await expect(card.getByText(/after your plan change/)).toHaveCount(0);
  });
});

const FREE_SCHEDULED = {
  planCode: 'free',
  nameCs: 'Free',
  nameEn: 'Free',
  nameDe: 'Free',
  startsAt: PERIOD_END,
  priceLines: [],
};

trainerTest.describe('settings plan card: cancelling a scheduled change', () => {
  trainerTest('confirming cancels the change, refetches and shows the toast', async ({ page }) => {
    await mockSubscription(
      page,
      subscription({ cancelAtPeriodEnd: true, scheduledPlan: { ...FREE_SCHEDULED, cancellable: true } }),
    );
    const cancelCalls: string[] = [];
    await page.route(/\/billing\/scheduled-change\/cancel$/, async (route) => {
      cancelCalls.push(route.request().method());
      await mockSubscription(page, subscription({}));
      return json(route, { kind: 'SwitchToFree', planCode: 'free' });
    });
    await page.goto('/settings#plan-billing');

    await expect(page.getByText(/^Free begins on .*2026$/)).toBeVisible();
    await page.getByRole('button', { name: 'Cancel change' }).click();
    const dialog = page.getByRole('dialog', { name: 'Cancel the scheduled change?' });
    await expect(dialog.getByText(/You stay on Pro and it renews on .*2026\. Nothing is charged now\./)).toBeVisible();
    await dialog.getByRole('button', { name: 'Cancel change' }).click();

    await expect(toastTitle(page, 'Change cancelled. You stay on Pro.')).toBeVisible();
    await expect(page.getByText(/ begins on /)).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Cancel change' })).toHaveCount(0);
    expect(cancelCalls).toEqual(['POST']);
  });

  trainerTest('the action is hidden when the change cannot be cancelled', async ({ page }) => {
    await mockSubscription(
      page,
      subscription({ cancelAtPeriodEnd: true, scheduledPlan: { ...FREE_SCHEDULED, cancellable: false } }),
    );
    await page.goto('/settings#plan-billing');

    await expect(page.getByText(/^Free begins on .*2026$/)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Cancel change' })).toHaveCount(0);
  });

  trainerTest('a provider error shows its message and keeps the scheduled line', async ({ page }) => {
    await mockSubscription(
      page,
      subscription({ cancelAtPeriodEnd: true, scheduledPlan: { ...FREE_SCHEDULED, cancellable: true } }),
    );
    await page.route(/\/billing\/scheduled-change\/cancel$/, (route) =>
      route.fulfill({
        status: 502,
        contentType: 'application/problem+json',
        body: JSON.stringify({ status: 502, title: 'Bad Gateway', errorCode: 'BILLING_PROVIDER_ERROR' }),
      }),
    );
    await page.goto('/settings#plan-billing');

    await page.getByRole('button', { name: 'Cancel change' }).click();
    await page.getByRole('dialog', { name: 'Cancel the scheduled change?' }).getByRole('button', { name: 'Cancel change' }).click();

    await expect(toastTitle(page, "The payment provider didn't answer. Please try again in a moment.")).toBeVisible();
    await expect(page.getByText(/^Free begins on .*2026$/)).toBeVisible();
  });
});

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
  trainerTest('opens the portal in a new tab without an opener and keeps the app on Settings', async ({ page }) => {
    await mockSubscription(page, subscription({}));
    await mockPortal(page, 200);
    await page.goto('/settings#plan-billing');

    const popupPromise = page.waitForEvent('popup');
    await page.getByRole('button', { name: 'Manage billing' }).first().click();
    const popup = await popupPromise;

    await expect(popup).toHaveURL(PORTAL_URL);
    expect(await popup.evaluate(() => window.opener)).toBeNull();
    await expect(page).toHaveURL(/\/settings/);
  });

  trainerTest('a failed portal session closes the blank tab and shows the error toast', async ({ page, context }) => {
    await mockSubscription(page, subscription({}));
    await mockPortal(page, 502);
    await page.goto('/settings#plan-billing');

    await page.getByRole('button', { name: 'Manage billing' }).first().click();

    await expect(toastTitle(page, "The payment provider didn't answer. Please try again in a moment.")).toBeVisible();
    await expect.poll(() => context.pages().length).toBe(1);
    await expect(page).toHaveURL(/\/settings/);
  });

  trainerTest('coming back to the app tab shows the subscription changed in the portal', async ({ page }) => {
    await mockSubscription(page, subscription({ nameEn: 'Pro' }));
    await mockPortal(page, 200);
    await page.goto('/settings#plan-billing');

    const popupPromise = page.waitForEvent('popup');
    await page.getByRole('button', { name: 'Manage billing' }).first().click();
    await expect(await popupPromise).toHaveURL(PORTAL_URL);

    await mockSubscription(page, subscription({ nameEn: 'Studio' }));
    await page.evaluate(() => window.dispatchEvent(new Event('focus')));

    await expect(page.getByRole('heading', { name: 'Studio' })).toBeVisible();
  });

  trainerTest('a blocked pop-up falls back to opening the portal in the same tab', async ({ page }) => {
    await page.addInitScript(() => {
      window.open = () => null;
    });
    await mockSubscription(page, subscription({}));
    await mockPortal(page, 200);
    await page.goto('/settings#plan-billing');

    await page.getByRole('button', { name: 'Manage billing' }).first().click();

    await page.waitForURL(PORTAL_URL);
  });
});
