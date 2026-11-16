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

  trainerTest('without a scheduled change the card keeps its renewal line', async ({ page }) => {
    await mockSubscription(page, subscription({}));
    await page.goto('/settings#plan-billing');

    await expect(page.getByText(/Monthly · Renews .*2026/)).toBeVisible();
    await expect(page.getByText(/ begins on /)).toHaveCount(0);
  });
});
