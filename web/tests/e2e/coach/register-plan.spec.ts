/**
 * Sign-up plan step (/register/plan): a one-role coach sees the one-role prices with "Second role later +X", a
 * two-role coach sees the combined prices, the breakdown note, "Second role included +X", "Both roles included"
 * and the roles line, and a paid plan posts includeSecondRoleAddOn from the roles. The subscription, plan and role
 * reads are mocked, so the signed-in fixture's real roles do not matter.
 */
import type { Page, Route } from '@playwright/test';
import { trainerTest, expect } from '../fixtures/auth';

const CHECKOUT_URL = 'https://checkout.stripe.test/c/pay/cs_test_e2e_register';

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

/** Display prices after a 30 % promotion: the server sends them, the page never computes them. */
const DISCOUNTED_MINOR_UNITS: Record<string, { price: number; addOn?: number }> = {
  start: { price: 20900, addOn: 10500 },
  pro: { price: 41900, addOn: 21000 },
  studio: { price: 69900 },
};

type CoachRole = 'Trainer' | 'Nutritionist';

function json(route: Route, body: unknown, status = 200, contentType = 'application/json') {
  return route.fulfill({ status, contentType, body: JSON.stringify(body) });
}

interface MockOptions {
  roles: CoachRole[];
  promotion?: boolean;
  /** Holds the roles response until released, to prove the cards wait for it. */
  rolesGate?: Promise<void>;
  /** Fails the roles lookup. */
  rolesFail?: boolean;
}

async function mockReads(page: Page, { roles, promotion = false, rolesGate, rolesFail = false }: MockOptions) {
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
      ...(promotion && {
        promotion: {
          name: 'Launch offer',
          kind: 'Percent',
          value: 30,
          duration: 'Months',
          durationMonths: 3,
          claimUntil: '2026-12-31T12:00:00Z',
          planCodes: ['start', 'pro', 'studio'],
        },
      }),
      plans: PLANS.map((plan) => {
        const discounted = promotion && plan.code !== 'free' ? DISCOUNTED_MINOR_UNITS[plan.code] : undefined;
        return {
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
          ...(discounted
            ? {
                discountedPriceMinorUnits: discounted.price,
                discountedSecondRoleAddOnPriceMinorUnits: discounted.addOn,
              }
            : {}),
        };
      }),
    }),
  );
  await page.route(/\/users\/me\/roles$/, async (route) => {
    if (route.request().method() !== 'GET') return route.fallback();
    if (rolesGate) await rolesGate;
    if (rolesFail) return json(route, { status: 500, title: 'Server Error' }, 500, 'application/problem+json');
    return json(route, {
      roles: roles.map((role) => ({ role, clientCount: 0, sharedWithOtherRoleCount: 0 })),
      coachAccountActiveUntil: null,
      activeUntilIfDisabled: null,
    });
  });
}

trainerTest.describe('register plan step', () => {
  trainerTest('a two-role coach sees combined prices, the breakdown and the roles line', async ({ page }) => {
    await mockReads(page, { roles: ['Trainer', 'Nutritionist'] });
    await page.goto('/register/plan');

    await expect(
      page.getByText('You signed up as Personal trainer and Nutritionist. Prices below include your second role.'),
    ).toBeVisible();

    const start = page.getByRole('article', { name: 'Start' });
    await expect(start.getByText(/448/).first()).toBeVisible();
    await expect(start.getByText(/Start .*299.* \+ second role .*149/)).toBeVisible();
    await expect(start.getByText(/Second role included \+.*149/)).toBeVisible();
    await expect(start.getByText(/Second role later/)).toHaveCount(0);
    await expect(start.locator('s')).toHaveCount(0);

    await expect(page.getByRole('article', { name: 'Pro' }).getByText(/Second role included \+.*299/)).toBeVisible();
    await expect(page.getByRole('article', { name: 'Free' }).getByText('Both roles included')).toBeVisible();
    await expect(page.getByRole('article', { name: 'Studio' }).getByText('Both roles included')).toBeVisible();
  });

  trainerTest('a two-role coach sees the promotion on the combined price', async ({ page }) => {
    await mockReads(page, { roles: ['Trainer', 'Nutritionist'], promotion: true });
    await page.goto('/register/plan');

    const start = page.getByRole('article', { name: 'Start' });
    await expect(start.getByText(/314/).first()).toBeVisible();
    await expect(start.locator('s')).toContainText('448');
    await expect(start.getByText(/Start .*299.* \+ second role .*149.* · −30 % for 3 months, then .*448/)).toBeVisible();

    // Studio includes both roles, so its note carries the promotion only.
    const studio = page.getByRole('article', { name: 'Studio' });
    await expect(studio.locator('s')).toContainText('999');
    await expect(studio.getByText(/second role/i)).toHaveCount(0);
    await expect(studio.getByText(/−30 % for 3 months, then .*999/)).toBeVisible();
  });

  trainerTest('a one-role coach keeps the one-role prices and "Second role later"', async ({ page }) => {
    await mockReads(page, { roles: ['Trainer'] });
    await page.goto('/register/plan');

    const start = page.getByRole('article', { name: 'Start' });
    await expect(start.getByText(/299/).first()).toBeVisible();
    await expect(start.getByText(/Second role later \+.*149/)).toBeVisible();
    await expect(start.getByText(/Second role included/)).toHaveCount(0);
    await expect(page.getByText(/You signed up as/)).toHaveCount(0);
    await expect(page.getByRole('article', { name: 'Free' }).getByText('Add a second role any time, free')).toBeVisible();
  });

  trainerTest('the cards wait for the roles, so a two-role coach never sees one-role prices', async ({ page }) => {
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    await mockReads(page, { roles: ['Trainer', 'Nutritionist'], rolesGate: gate });
    await page.goto('/register/plan');

    // RoleGuard renders nothing while the roles lookup is pending, so no heading is expected here.
    await expect(page.getByRole('article')).toHaveCount(0);
    await expect(page.getByText(/Second role later/)).toHaveCount(0);

    release();
    const start = page.getByRole('article', { name: 'Start' });
    await expect(start.getByText(/448/).first()).toBeVisible();
    await expect(start.getByText(/Second role included/)).toBeVisible();
    await expect(page.getByText(/Second role later/)).toHaveCount(0);
  });

  trainerTest('a failed roles lookup sends the coach to /clients (the guard fails closed)', async ({ page }) => {
    await mockReads(page, { roles: ['Trainer', 'Nutritionist'], rolesFail: true });
    await page.goto('/register/plan');

    await expect(page).toHaveURL(/\/clients$/);
    await expect(page.getByRole('article')).toHaveCount(0);
  });

  trainerTest('a two-role coach choosing Pro posts includeSecondRoleAddOn true', async ({ page }) => {
    await mockReads(page, { roles: ['Trainer', 'Nutritionist'] });
    let checkoutBody: unknown = null;
    await page.route(/\/billing\/checkout-session$/, (route) => {
      checkoutBody = route.request().postDataJSON();
      return json(route, { url: CHECKOUT_URL });
    });
    // The fake Stripe page: nothing leaves the machine.
    await page.route('https://checkout.stripe.test/**', (route) =>
      route.fulfill({ status: 200, contentType: 'text/html', body: '<h1>Stripe Checkout stand-in</h1>' }),
    );

    await page.goto('/register/plan');
    await page.getByRole('article', { name: 'Pro' }).getByRole('button', { name: 'Choose Pro' }).click();

    await page.waitForURL(`${CHECKOUT_URL}**`);
    expect(checkoutBody).toEqual({ planCode: 'pro', includeSecondRoleAddOn: true, flow: 'SignUp' });
  });

  trainerTest('billing not configured shows a localized toast and stays on the sign-up plan step', async ({ page }) => {
    await mockReads(page, { roles: ['Trainer', 'Nutritionist'] });
    await page.route(/\/billing\/checkout-session$/, (route) =>
      json(
        route,
        { status: 503, title: 'Service Unavailable', errorCode: 'BILLING_NOT_CONFIGURED' },
        503,
        'application/problem+json',
      ),
    );

    await page.goto('/register/plan');
    await page.getByRole('article', { name: 'Start' }).getByRole('button', { name: 'Choose Start' }).click();

    await expect(page.getByText("Billing isn't available right now. Please try again later.")).toBeVisible();
    await expect(page).toHaveURL(/\/register\/plan$/);
  });
});
