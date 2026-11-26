/**
 * Sign-up plan step (/register/plan) layout: what only a real browser can measure. The prices for one and two roles,
 * the promotion, the checkout request and the roles guard are proven by the component tests in
 * src/pages/RegisterPlanPage.test.tsx. The subscription, plan and role reads are mocked, so the signed-in fixture's
 * real roles do not matter.
 */
import type { Page, Route } from '@playwright/test';
import { trainerTest, expect } from '../fixtures/auth';

interface PlanMock {
  code: string;
  nameEn: string;
  priceMinorUnits: number;
  secondRoleAddOnPriceMinorUnits?: number;
  bothRolesIncluded: boolean;
  maxActiveClients?: number;
  canSendQuestionnaires: boolean;
}

const PLANS: PlanMock[] = [
  { code: 'free', nameEn: 'Free', priceMinorUnits: 0, bothRolesIncluded: true, maxActiveClients: 2, canSendQuestionnaires: false },
  {
    code: 'start',
    nameEn: 'Start',
    priceMinorUnits: 29900,
    secondRoleAddOnPriceMinorUnits: 14900,
    bothRolesIncluded: false,
    maxActiveClients: 10,
    canSendQuestionnaires: false,
  },
  {
    code: 'pro',
    nameEn: 'Pro',
    priceMinorUnits: 59900,
    secondRoleAddOnPriceMinorUnits: 29900,
    bothRolesIncluded: false,
    maxActiveClients: 30,
    canSendQuestionnaires: true,
  },
  { code: 'studio', nameEn: 'Studio', priceMinorUnits: 99900, bothRolesIncluded: true, canSendQuestionnaires: true },
];

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

/** Mocks the reads behind the step for a one-role coach on Free; every paid plan has an annual price (10 × monthly). */
async function mockReads(page: Page) {
  await page.route(/\/users\/me\/subscription$/, (route) =>
    json(route, {
      planCode: 'free',
      nameCs: 'Free',
      nameEn: 'Free',
      nameDe: 'Free',
      taxMode: 'None',
      hasBillingAccount: false,
      cancelAtPeriodEnd: false,
      isSetByFormUp: false,
      isLapsed: false,
      planChoiceRequired: false,
      keepClientsChoiceRequired: false,
      overCapClientCount: 0,
      freeSlotCount: 0,
      activeClientCount: 0,
      clientLimit: 2,
      secondRole: 'Included',
      priceLines: [],
      features: {
        canCreatePlans: true,
        canMessage: true,
        canSendQuestionnaires: false,
        canUseWeeklyCheckIns: false,
        canUsePerClientCheckInConfig: false,
        canUsePayments: false,
      },
    }),
  );
  await page.route(/\/users\/me\/subscription\/plans$/, (route) =>
    json(route, {
      taxMode: 'None',
      plans: PLANS.map((plan) => ({
        ...plan,
        nameCs: plan.nameEn,
        nameDe: plan.nameEn,
        currency: 'CZK',
        billingInterval: 'Monthly',
        canCreatePlans: true,
        canMessage: true,
        canUseWeeklyCheckIns: plan.canSendQuestionnaires,
        canUsePerClientCheckInConfig: plan.code === 'studio',
        canUsePayments: false,
        ...(plan.code !== 'free' ? { annualPriceMinorUnits: plan.priceMinorUnits * 10, annualSavingPercent: 17 } : {}),
      })),
    }),
  );
  await page.route(/\/users\/me\/roles$/, (route) =>
    route.request().method() === 'GET'
      ? json(route, {
          roles: [{ role: 'Trainer', clientCount: 0, sharedWithOtherRoleCount: 0 }],
          coachAccountActiveUntil: null,
          activeUntilIfDisabled: null,
        })
      : route.fallback(),
  );
}

trainerTest.describe('register plan step layout', () => {
  // Real-browser layout: the switch must not slide when the annual note appears, and the shared card rows
  // (CSS subgrid) must keep every card's button on one line without reserved empty space.
  trainerTest('the switch stays put when Annual adds its note, and the card buttons line up', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await mockReads(page);
    await page.goto('/register/plan');

    const switchGroup = page.getByRole('group', { name: 'Billing period' });
    const monthly = switchGroup.getByRole('button', { name: 'Monthly' });
    const buttonTops = async () =>
      Promise.all(
        ['Free', 'Start', 'Pro', 'Studio'].map(async (name) => {
          const box = await page.getByRole('article', { name }).getByRole('button').first().boundingBox();
          return Math.round(box!.y);
        }),
      );

    await expect(page.getByRole('article')).toHaveCount(4);
    const before = await monthly.boundingBox();
    expect(new Set(await buttonTops()).size).toBe(1);

    await switchGroup.getByRole('button', { name: /^Annual/ }).click();
    await expect(page.getByRole('article', { name: 'Start' }).getByText(/2.990/).first()).toBeVisible();

    const after = await monthly.boundingBox();
    expect(Math.round(after!.x)).toBe(Math.round(before!.x));
    expect(new Set(await buttonTops()).size).toBe(1);
  });
});
