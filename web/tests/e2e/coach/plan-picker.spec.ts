/**
 * Plan picker: the four plan cards render with the coach's plan outlined, the VAT line follows the server's tax mode,
 * a paid plan sends the coach to the checkout URL the server returns (no real Stripe), and a billing error
 * shows as a localized toast without leaving the page. The subscription, plan and role reads are mocked so the
 * spec does not depend on the seeded plan catalogue or on Stripe being configured in the harness.
 */
import type { Page, Route } from '@playwright/test';
import { trainerTest, expect } from '../fixtures/auth';

const CHECKOUT_URL = 'https://checkout.stripe.test/c/pay/cs_test_e2e';

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

function json(route: Route, body: unknown, status = 200, contentType = 'application/json') {
  return route.fulfill({ status, contentType, body: JSON.stringify(body) });
}

/** Display prices after a 30 % automatic promotion: the server sends them, the page never computes them. */
const DISCOUNTED_MINOR_UNITS: Record<string, { price: number; addOn?: number }> = {
  start: { price: 20900, addOn: 10400 },
  pro: { price: 41900, addOn: 20900 },
  studio: { price: 69900 },
};

/** Which plans the mocked automatic promotion covers: all paid plans, or only Pro. */
type OfferCoverage = 'all' | 'proOnly';

/** Mocks the reads behind the picker: a coach on Free with one role, and the four plans (with an offer, if asked). */
async function mockPickerReads(
  page: Page,
  taxMode: 'None' | 'Vat21',
  offer?: OfferCoverage,
  subscription: Record<string, unknown> = {},
) {
  await page.route(/\/users\/me\/subscription$/, (route) =>
    json(route, {
      planCode: 'free',
      nameCs: 'Free',
      nameEn: 'Free',
      nameDe: 'Free',
      taxMode,
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
      ...subscription,
    }),
  );
  await page.route(/\/users\/me\/subscription\/plans$/, (route) =>
    json(route, {
      taxMode,
      ...(offer && {
        promotion: {
          name: 'Launch offer',
          kind: 'Percent',
          value: 30,
          duration: 'Months',
          durationMonths: 3,
          claimUntil: '2026-12-31T12:00:00Z',
          planCodes: offer === 'all' ? ['start', 'pro', 'studio'] : ['pro'],
        },
      }),
      plans: PLANS.map((plan) => {
        const covered = offer !== undefined && (offer === 'all' || plan.code === 'pro') && plan.code !== 'free';
        const discounted = DISCOUNTED_MINOR_UNITS[plan.code];
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
          ...(covered && discounted
            ? {
                discountedPriceMinorUnits: discounted.price,
                discountedSecondRoleAddOnPriceMinorUnits: discounted.addOn,
              }
            : {}),
        };
      }),
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

trainerTest.describe('plan picker', () => {
  trainerTest('shows four plans, outlines the current one and shows the VAT line only for Vat21', async ({ page }) => {
    await mockPickerReads(page, 'Vat21');
    await page.goto('/plan');

    await expect(page.getByRole('heading', { name: 'Choose your plan', level: 1 })).toBeVisible();
    for (const name of ['Free', 'Start', 'Pro', 'Studio']) {
      await expect(page.getByRole('article', { name })).toBeVisible();
    }

    const free = page.getByRole('article', { name: 'Free' });
    await expect(free.getByText('Your plan', { exact: true }).first()).toBeVisible();
    await expect(free.getByRole('button', { name: 'Your plan' })).toBeDisabled();
    await expect(page.getByRole('article', { name: 'Pro' }).getByRole('button', { name: 'Select Pro' })).toBeEnabled();
    await expect(page.getByRole('link', { name: 'Back to settings' })).toBeVisible();
    await expect(page.getByText('Have a code? Enter it at checkout.')).toBeVisible();
    await expect(page.getByText('Prices without VAT, 21 % VAT is added.')).toBeVisible();
  });

  trainerTest('shows no VAT line when the server tax mode is None', async ({ page }) => {
    await mockPickerReads(page, 'None');
    await page.goto('/plan');

    await expect(page.getByRole('article', { name: 'Pro' })).toBeVisible();
    await expect(page.getByText(/Prices without VAT/)).toHaveCount(0);
  });

  trainerTest('shows no banner and no crossed-out price when no promotion applies', async ({ page }) => {
    await mockPickerReads(page, 'Vat21');
    await page.goto('/plan');

    await expect(page.getByRole('article', { name: 'Pro' })).toBeVisible();
    await expect(page.getByText(/Launch offer/)).toHaveCount(0);
    await expect(page.locator('article s')).toHaveCount(0);
  });

  trainerTest('an automatic promotion shows the banner and crossed-out prices on every paid plan', async ({ page }) => {
    await mockPickerReads(page, 'Vat21', 'all');
    await page.goto('/plan');

    const banner = page.getByRole('status').filter({ hasText: 'Launch offer' });
    await expect(banner.getByText('Launch offer: −30 % for your first 3 months')).toBeVisible();
    await expect(banner.getByText(/Applied at checkout on every paid plan\. Claim it by .*2026\./)).toBeVisible();

    for (const name of ['Start', 'Pro', 'Studio']) {
      const card = page.getByRole('article', { name });
      await expect(card.locator('s')).toHaveCount(1);
      await expect(card.getByText('−30 % for 3 months')).toBeVisible();
    }
    // The server's discounted price is the big one; the plan's own price is the crossed-out one.
    const pro = page.getByRole('article', { name: 'Pro' });
    await expect(pro.locator('s')).toContainText('599');
    await expect(pro.getByText(/419/)).toBeVisible();

    // Free is never discounted.
    await expect(page.getByRole('article', { name: 'Free' }).locator('s')).toHaveCount(0);
  });

  trainerTest('a promotion that covers only Pro crosses out only the Pro price and names the plan', async ({ page }) => {
    await mockPickerReads(page, 'Vat21', 'proOnly');
    await page.goto('/plan');

    await expect(page.getByText(/Applied at checkout on Pro\./)).toBeVisible();
    await expect(page.getByRole('article', { name: 'Pro' }).locator('s')).toHaveCount(1);
    await expect(page.getByRole('article', { name: 'Start' }).locator('s')).toHaveCount(0);
    await expect(page.getByRole('article', { name: 'Studio' }).locator('s')).toHaveCount(0);
  });

  trainerTest('a paid plan posts the checkout request and follows the returned URL', async ({ page }) => {
    await mockPickerReads(page, 'Vat21');
    let checkoutBody: unknown = null;
    await page.route(/\/billing\/checkout-session$/, (route) => {
      checkoutBody = route.request().postDataJSON();
      return json(route, { url: CHECKOUT_URL });
    });
    // The fake Stripe page: nothing leaves the machine.
    await page.route('https://checkout.stripe.test/**', (route) =>
      route.fulfill({ status: 200, contentType: 'text/html', body: '<h1>Stripe Checkout stand-in</h1>' }),
    );

    await page.goto('/plan');
    await page.getByRole('article', { name: 'Pro' }).getByRole('button', { name: 'Select Pro' }).click();
    await page.getByRole('region', { name: 'Selected plan' }).getByRole('button', { name: 'Upgrade to Pro' }).click();

    await page.waitForURL(`${CHECKOUT_URL}**`);
    await expect(page.getByRole('heading', { name: 'Stripe Checkout stand-in' })).toBeVisible();
    expect(checkoutBody).toEqual({ planCode: 'pro', includeSecondRoleAddOn: false, flow: 'Settings' });
  });

  trainerTest('billing not configured shows a localized toast and stays on the picker', async ({ page }) => {
    await mockPickerReads(page, 'Vat21');
    await page.route(/\/billing\/checkout-session$/, (route) =>
      json(
        route,
        { status: 503, title: 'Service Unavailable', errorCode: 'BILLING_NOT_CONFIGURED' },
        503,
        'application/problem+json',
      ),
    );

    await page.goto('/plan');
    await page.getByRole('article', { name: 'Start' }).getByRole('button', { name: 'Select Start' }).click();
    const bar = page.getByRole('region', { name: 'Selected plan' });
    await bar.getByRole('button', { name: 'Upgrade to Start' }).click();

    await expect(page.getByText("Billing isn't available right now. Please try again later.")).toBeVisible();
    await expect(page).toHaveURL(/\/plan$/);
    await expect(page.getByRole('article', { name: 'Start' }).getByRole('button', { name: 'Selected' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    await expect(bar.getByRole('button', { name: 'Upgrade to Start' })).toBeEnabled();
  });

  trainerTest('selecting a card, by its button or its body, starts nothing until the bar is confirmed', async ({ page }) => {
    await mockPickerReads(page, 'Vat21');
    const billingCalls: string[] = [];
    await page.route(/\/billing\/(checkout-session|change-plan|switch-to-free)$/, (route) => {
      billingCalls.push(route.request().url());
      return route.fulfill({ status: 500, contentType: 'application/json', body: '{}' });
    });

    await page.goto('/plan');
    const bar = page.getByRole('region', { name: 'Selected plan' });
    await expect(page.getByRole('article', { name: 'Pro' })).toBeVisible();
    // Nothing is preselected on /plan, so there is no bar.
    await expect(bar).toHaveCount(0);

    const pro = page.getByRole('article', { name: 'Pro' });
    await pro.getByRole('button', { name: 'Select Pro' }).click();
    await expect(bar).toBeVisible();
    await expect(bar.getByText('Pro · ', { exact: false })).toBeVisible();
    await expect(pro.getByRole('button', { name: 'Selected' })).toHaveAttribute('aria-pressed', 'true');

    // A click on the card body moves the selection and still starts nothing.
    await page.getByRole('article', { name: 'Studio' }).getByRole('heading', { name: 'Studio' }).click();
    await expect(page.getByRole('article', { name: 'Studio' }).getByRole('button', { name: 'Selected' })).toBeVisible();
    await expect(pro.getByRole('button', { name: 'Select Pro' })).toHaveAttribute('aria-pressed', 'false');
    await expect(bar.getByRole('button', { name: 'Upgrade to Studio' })).toBeEnabled();
    expect(billingCalls).toEqual([]);
  });

  trainerTest('the current plan is not selectable and the bar spells out the promotion price', async ({ page }) => {
    await mockPickerReads(page, 'Vat21', 'all');
    await page.goto('/plan');

    const free = page.getByRole('article', { name: 'Free' });
    await expect(free.getByRole('button', { name: 'Your plan' })).toBeDisabled();
    await free.getByRole('heading', { name: 'Free' }).click();
    await expect(page.getByRole('region', { name: 'Selected plan' })).toHaveCount(0);

    await page.getByRole('article', { name: 'Pro' }).getByRole('button', { name: 'Select Pro' }).click();
    const bar = page.getByRole('region', { name: 'Selected plan' });
    await expect(bar.getByText(/Pro · .*419.* \/ month/)).toBeVisible();
    await expect(bar.getByText(/−30 % for 3 months, then .*599/)).toBeVisible();
    await expect(bar.getByText('Card and invoices on Stripe\'s secure pages')).toBeVisible();
  });

  trainerTest('the back icon button sits left of the title and opens Settings at Plan and billing', async ({ page }) => {
    await mockPickerReads(page, 'Vat21');
    await page.goto('/plan');

    const back = page.getByRole('link', { name: 'Back to settings' });
    await expect(back).toHaveAttribute('href', '/settings#plan-billing');
    // Icon only: no visible text, and it comes before the title in the layout.
    await expect(back).toHaveText('');
    const backBox = await back.boundingBox();
    const titleBox = await page.getByRole('heading', { name: 'Choose your plan', level: 1 }).boundingBox();
    expect(backBox).not.toBeNull();
    expect(titleBox).not.toBeNull();
    expect(backBox!.x + backBox!.width).toBeLessThanOrEqual(titleBox!.x);

    await back.click();
    await expect(page).toHaveURL(/\/settings#plan-billing$/);
  });

  trainerTest('the confirm bar is pinned to the bottom of the viewport even when the page is short', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 1600 });
    await mockPickerReads(page, 'Vat21');
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

const PERIOD_END = '2026-12-01T00:00:00Z';

/** A coach with a live paid subscription on the given plan; the plan name doubles as the "You keep" name. */
function liveSubscription(plan: 'start' | 'pro', extra: Record<string, unknown> = {}) {
  const name = plan === 'pro' ? 'Pro' : 'Start';
  return {
    planCode: plan,
    nameCs: name,
    nameEn: name,
    nameDe: name,
    status: 'Active',
    hasBillingAccount: true,
    currentPeriodEndsAt: PERIOD_END,
    secondRole: 'NotAvailable',
    ...extra,
  };
}

function previewBody(overrides: Record<string, unknown>) {
  return {
    planCode: 'pro',
    timing: 'Now',
    billingInterval: 'Monthly',
    currency: 'CZK',
    todayAmountMinorUnits: 15800,
    todayTaxMinorUnits: 0,
    nextAmountMinorUnits: 59900,
    nextChargeAt: PERIOD_END,
    cancelsScheduledSwitchToFree: false,
    taxMode: 'Vat21',
    ...overrides,
  };
}

trainerTest.describe('plan picker: changing a live paid plan', () => {
  trainerTest('an upgrade shows today\'s charge from the preview and sends one attempt id per attempt', async ({ page }) => {
    await mockPickerReads(page, 'Vat21', undefined, liveSubscription('start'));
    const previewBodies: unknown[] = [];
    await page.route(/\/billing\/change-plan\/preview$/, (route) => {
      previewBodies.push(route.request().postDataJSON());
      return json(route, previewBody({}));
    });
    const changeBodies: { planCode: string; attemptId: string }[] = [];
    await page.route(/\/billing\/change-plan$/, (route) => {
      changeBodies.push(route.request().postDataJSON());
      return changeBodies.length === 1
        ? json(
            route,
            { status: 502, title: 'Bad Gateway', errorCode: 'BILLING_PROVIDER_ERROR' },
            502,
            'application/problem+json',
          )
        : json(route, { planCode: 'pro', timing: 'Now', effectiveAt: '2026-11-16T10:00:00Z' });
    });

    await page.goto('/plan');
    await page.getByRole('article', { name: 'Pro' }).getByRole('button', { name: 'Select Pro' }).click();
    const bar = page.getByRole('region', { name: 'Selected plan' });
    await expect(bar.getByText(/Today about .*158.* for the rest of this month, then .*599.* \/ month from .*2026/)).toBeVisible();
    await expect(bar.getByText('Your switch to Free will be cancelled.')).toHaveCount(0);
    expect(previewBodies).toEqual([{ planCode: 'pro' }]);

    // A declined charge ends that attempt: the retry carries a new id.
    await bar.getByRole('button', { name: 'Upgrade to Pro' }).click();
    await expect(bar.getByRole('button', { name: 'Upgrade to Pro' })).toBeEnabled();
    await bar.getByRole('button', { name: 'Upgrade to Pro' }).click();
    await expect.poll(() => changeBodies.length).toBe(2);
    expect(changeBodies[0].planCode).toBe('pro');
    expect(changeBodies[0].attemptId).toMatch(/^[0-9a-f-]{36}$/);
    expect(changeBodies[1].attemptId).toMatch(/^[0-9a-f-]{36}$/);
    expect(changeBodies[1].attemptId).not.toBe(changeBodies[0].attemptId);
  });

  trainerTest('an annual preview says the rest of this year and bills per year', async ({ page }) => {
    await mockPickerReads(page, 'Vat21', undefined, liveSubscription('start'));
    await page.route(/\/billing\/change-plan\/preview$/, (route) =>
      json(route, previewBody({ billingInterval: 'Annual', nextAmountMinorUnits: 599000 })),
    );

    await page.goto('/plan');
    await page.getByRole('article', { name: 'Pro' }).getByRole('button', { name: 'Select Pro' }).click();
    const bar = page.getByRole('region', { name: 'Selected plan' });
    await expect(bar.getByText(/Today about .*158.* for the rest of this year, then .*5.?990.* \/ year from .*2026/)).toBeVisible();
    await expect(bar.getByText(/rest of this month/)).toHaveCount(0);
  });

  trainerTest('the bar shows an error hint when the preview fails and confirming still works', async ({ page }) => {
    await mockPickerReads(page, 'Vat21', undefined, liveSubscription('start'));
    await page.route(/\/billing\/change-plan\/preview$/, (route) =>
      json(route, { status: 502, title: 'Bad Gateway', errorCode: 'BILLING_PROVIDER_ERROR' }, 502, 'application/problem+json'),
    );

    await page.goto('/plan');
    await page.getByRole('article', { name: 'Pro' }).getByRole('button', { name: 'Select Pro' }).click();
    const bar = page.getByRole('region', { name: 'Selected plan' });
    await expect(bar.getByText("Couldn't calculate today's charge. You can still confirm the change.")).toBeVisible();
    await expect(bar.getByText(/Today about/)).toHaveCount(0);
    await expect(bar.getByText('Your switch to Free will be cancelled.')).toHaveCount(0);
    await expect(bar.getByRole('button', { name: 'Upgrade to Pro' })).toBeEnabled();
  });

  trainerTest('an upgrade that drops a scheduled switch to Free says so in the bar', async ({ page }) => {
    await mockPickerReads(
      page,
      'Vat21',
      undefined,
      liveSubscription('start', {
        cancelAtPeriodEnd: true,
        scheduledPlan: { planCode: 'free', nameCs: 'Free', nameEn: 'Free', nameDe: 'Free', startsAt: PERIOD_END, priceLines: [] },
      }),
    );
    await page.route(/\/billing\/change-plan\/preview$/, (route) =>
      json(route, previewBody({ cancelsScheduledSwitchToFree: true })),
    );

    await page.goto('/plan');
    await page.getByRole('article', { name: 'Pro' }).getByRole('button', { name: 'Select Pro' }).click();
    const bar = page.getByRole('region', { name: 'Selected plan' });
    await expect(bar.getByText(/Today about .*158/)).toBeVisible();
    await expect(bar.getByText('Your switch to Free will be cancelled.')).toBeVisible();
  });

  trainerTest('a downgrade and a switch to Free say when they start and what the coach keeps until then', async ({ page }) => {
    await mockPickerReads(page, 'Vat21', undefined, liveSubscription('pro'));
    await page.route(/\/billing\/change-plan\/preview$/, (route) =>
      json(
        route,
        previewBody({
          planCode: 'start',
          timing: 'AtNextRenewal',
          todayAmountMinorUnits: 0,
          nextAmountMinorUnits: 29900,
        }),
      ),
    );

    await page.goto('/plan');
    const bar = page.getByRole('region', { name: 'Selected plan' });

    await page.getByRole('article', { name: 'Start' }).getByRole('button', { name: 'Select Start' }).click();
    await expect(bar.getByText(/Start starts on .*2026\. You keep Pro until then\./)).toBeVisible();
    await expect(bar.getByText(/Today about/)).toHaveCount(0);

    await page.getByRole('article', { name: 'Free' }).getByRole('button', { name: /Select Free/ }).click();
    await expect(bar.getByText(/Free starts on .*2026\. You keep Pro until then\./)).toBeVisible();
  });

  trainerTest('a coach on Free keeps the checkout hint and makes no preview call', async ({ page }) => {
    await mockPickerReads(page, 'Vat21');
    const previewCalls: string[] = [];
    await page.route(/\/billing\/change-plan\/preview$/, (route) => {
      previewCalls.push(route.request().url());
      return route.fulfill({ status: 500, contentType: 'application/json', body: '{}' });
    });

    await page.goto('/plan');
    await page.getByRole('article', { name: 'Pro' }).getByRole('button', { name: 'Select Pro' }).click();
    await expect(page.getByRole('region', { name: 'Selected plan' }).getByText("Card and invoices on Stripe's secure pages")).toBeVisible();
    expect(previewCalls).toEqual([]);
  });
});
