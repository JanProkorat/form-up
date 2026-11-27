/**
 * Plan picker layout: what only a real browser can measure. The cards, prices, promotions, plan changes and
 * their errors are proven by the component tests in src/pages/PlanPickerPage.test.tsx. The subscription, plan and
 * role reads are mocked so the spec does not depend on the seeded plan catalogue or on Stripe being configured.
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

/** Mocks the reads behind the picker: a coach on Free with one role, and the four plans. */
async function mockPickerReads(page: Page) {
  await page.route(/\/users\/me\/subscription$/, (route) =>
    json(route, {
      planCode: 'free',
      nameCs: 'Free',
      nameEn: 'Free',
      nameDe: 'Free',
      taxMode: 'Vat21',
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
      taxMode: 'Vat21',
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

trainerTest.describe('plan picker layout', () => {
  // Real-browser geometry: the icon sits left of the title. Where it leads is asserted in PlanPickerPage.test.tsx.
  trainerTest('the back icon button sits left of the title', async ({ page }) => {
    await mockPickerReads(page);
    await page.goto('/plan');

    const back = page.getByRole('link', { name: 'Back to settings' });
    const backBox = await back.boundingBox();
    const titleBox = await page.getByRole('heading', { name: 'Choose your plan', level: 1 }).boundingBox();
    expect(backBox).not.toBeNull();
    expect(titleBox).not.toBeNull();
    expect(backBox!.x + backBox!.width).toBeLessThanOrEqual(titleBox!.x);
  });

  // Real-browser geometry: a fixed bar and the page footer need a layout engine.
  trainerTest('the confirm bar is pinned to the bottom of the viewport even when the page is short', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 1600 });
    await mockPickerReads(page);
    await page.goto('/plan');

    await page.getByRole('article', { name: 'Pro' }).getByRole('button', { name: 'Select Pro' }).click();
    const bar = page.getByRole('region', { name: 'Selected plan' });
    await expect(bar).toBeVisible();
    const viewport = page.viewportSize();
    await expect
      .poll(async () => {
        const box = await bar.boundingBox();
        return box ? Math.round(box.y + box.height) : null;
      })
      .toBe(viewport?.height);
    // The last row of the page stays above the bar.
    const footer = page.getByText(/^Billed monthly\./);
    const footerBox = await footer.boundingBox();
    const barBox = await bar.boundingBox();
    expect(footerBox!.y + footerBox!.height).toBeLessThanOrEqual(barBox!.y);
  });
});
