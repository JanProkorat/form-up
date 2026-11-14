/**
 * The e2e test data has no admin account yet, so this spec fakes the Admin role (see below) instead of
 * signing in as a real admin.
 *
 * Admin Promotions: the list renders with its columns, the status tabs ask the server for that tab, and the
 * new-promotion drawer creates a promotion (the code field shows only with a code) and flags a taken code inline.
 *
 * The e2e fixture has no Admin account and no promotions, so the spec signs in as the QA trainer, lets the roles
 * read report Admin (the guard reads Admin from there too), and mocks the /admin/promotions reads and writes.
 * Nothing reaches Stripe or the backend's promotion tables.
 */
import type { Page, Route } from '@playwright/test';
import { trainerTest, expect } from '../fixtures/auth';

interface PromotionMock {
  promotionId: string;
  name: string;
  kind: 'Percent' | 'FixedAmount';
  value: number;
  currency?: string;
  mode: 'Automatic' | 'Code';
  code?: string;
  duration: 'FirstMonth' | 'Months' | 'Forever';
  durationMonths?: number;
  claimFrom: string;
  claimUntil?: string;
  planCodes: string[];
  maxUses?: number;
  usedCount: number;
  newCoachesOnly: boolean;
  oneUsePerCoach: boolean;
  status: 'Active' | 'Scheduled' | 'Ended';
  createdAt: string;
}

const ALL_PLANS = ['start', 'pro', 'studio'];

const PROMOTIONS: PromotionMock[] = [
  {
    promotionId: '00000000-0000-4000-8000-000000000001',
    name: 'Launch offer',
    kind: 'Percent',
    value: 30,
    mode: 'Automatic',
    duration: 'Months',
    durationMonths: 3,
    claimFrom: '2026-11-01T00:00:00Z',
    claimUntil: '2026-12-31T22:59:59Z',
    planCodes: ALL_PLANS,
    usedCount: 12,
    newCoachesOnly: false,
    oneUsePerCoach: true,
    status: 'Active',
    createdAt: '2026-10-20T10:00:00Z',
  },
  {
    promotionId: '00000000-0000-4000-8000-000000000002',
    name: 'Partner code',
    kind: 'Percent',
    value: 20,
    mode: 'Code',
    code: 'COACH20',
    duration: 'Forever',
    claimFrom: '2026-09-01T00:00:00Z',
    planCodes: ALL_PLANS,
    maxUses: 50,
    usedCount: 18,
    newCoachesOnly: false,
    oneUsePerCoach: true,
    status: 'Active',
    createdAt: '2026-08-20T10:00:00Z',
  },
  {
    promotionId: '00000000-0000-4000-8000-000000000003',
    name: 'Black Friday',
    kind: 'Percent',
    value: 40,
    mode: 'Automatic',
    duration: 'Months',
    durationMonths: 6,
    claimFrom: '2026-11-27T00:00:00Z',
    claimUntil: '2026-12-01T22:59:59Z',
    planCodes: ALL_PLANS,
    usedCount: 0,
    newCoachesOnly: false,
    oneUsePerCoach: true,
    status: 'Scheduled',
    createdAt: '2026-10-25T10:00:00Z',
  },
  {
    promotionId: '00000000-0000-4000-8000-000000000004',
    name: 'Summer 2026',
    kind: 'Percent',
    value: 25,
    mode: 'Code',
    code: 'SUMMER',
    duration: 'Months',
    durationMonths: 2,
    claimFrom: '2026-07-01T00:00:00Z',
    claimUntil: '2026-08-31T21:59:59Z',
    planCodes: ['start', 'pro'],
    usedCount: 31,
    newCoachesOnly: false,
    oneUsePerCoach: true,
    status: 'Ended',
    createdAt: '2026-06-20T10:00:00Z',
  },
];

/** The visible toast with this text; the text also sits in a screen-reader live region, so a plain getByText is ambiguous. */
const toastTitle = (page: Page, text: string) => page.locator('[data-slot="toast-title"]').filter({ hasText: text });

function json(route: Route, body: unknown, status = 200, contentType = 'application/json') {
  return route.fulfill({ status, contentType, body: JSON.stringify(body) });
}

interface Harness {
  /** The status query value of every list read, in order. */
  listStatuses: string[];
  /** The JSON body of every create request, in order. */
  created: Record<string, unknown>[];
  /** Every end request: its path, content type and raw body, in order. */
  ended: { path: string; contentType: string | undefined; body: string | null }[];
}

interface Outcome {
  status: number;
  body?: unknown;
}

/**
 * Mocks the admin reads, the create and the end. `createOutcome` / `endOutcome` let a test turn the write into a
 * refusal; a successful create is added to the list and a successful end turns the row Ended, so the refetch after
 * either shows the change.
 */
async function mockAdminPromotions(
  page: Page,
  createOutcome: Outcome | null = null,
  endOutcome: Outcome | null = null,
): Promise<Harness> {
  const harness: Harness = { listStatuses: [], created: [], ended: [] };
  const promotions = PROMOTIONS.map((promotion) => ({ ...promotion }));

  await page.route(/\/users\/me\/roles$/, (route) =>
    route.request().method() === 'GET'
      ? json(route, {
          roles: [
            { role: 'Admin', clientCount: 0, sharedWithOtherRoleCount: 0 },
            { role: 'Trainer', clientCount: 0, sharedWithOtherRoleCount: 0 },
          ],
          coachAccountActiveUntil: null,
          activeUntilIfDisabled: null,
        })
      : route.fallback(),
  );

  await page.route(/\/admin\/promotions\/[0-9a-f-]+\/end$/, async (route) => {
    const request = route.request();
    harness.ended.push({
      path: new URL(request.url()).pathname,
      contentType: request.headers()['content-type'],
      body: request.postData(),
    });
    if (endOutcome) {
      const hasBody = endOutcome.body !== undefined;
      return route.fulfill({
        status: endOutcome.status,
        contentType: hasBody ? 'application/problem+json' : 'text/plain',
        body: hasBody ? JSON.stringify(endOutcome.body) : '',
      });
    }
    const id = new URL(request.url()).pathname.split('/').at(-2);
    const target = promotions.find((promotion) => promotion.promotionId === id);
    if (!target) {
      return route.fulfill({ status: 404, body: '' });
    }
    target.status = 'Ended';
    return json(route, target);
  });

  await page.route(/\/admin\/promotions(\?.*)?$/, async (route) => {
    // The page navigation to /admin/promotions is not an API call; only fetch and XHR are mocked.
    const resourceType = route.request().resourceType();
    if (resourceType !== 'fetch' && resourceType !== 'xhr') {
      return route.fallback();
    }

    if (route.request().method() === 'GET') {
      const status = new URL(route.request().url()).searchParams.get('status') ?? 'All';
      harness.listStatuses.push(status);
      const items = status === 'All' ? promotions : promotions.filter((promotion) => promotion.status === status);
      return json(route, { items });
    }

    const body = route.request().postDataJSON() as Record<string, unknown>;
    harness.created.push(body);
    if (createOutcome) {
      return json(route, createOutcome.body, createOutcome.status, 'application/problem+json');
    }
    const created: PromotionMock = {
      promotionId: '00000000-0000-4000-8000-000000000099',
      name: String(body['name']),
      kind: body['kind'] as PromotionMock['kind'],
      value: Number(body['value']),
      mode: body['mode'] as PromotionMock['mode'],
      code: body['code'] as string | undefined,
      duration: body['duration'] as PromotionMock['duration'],
      claimFrom: String(body['claimFrom']),
      planCodes: body['planCodes'] as string[],
      usedCount: 0,
      newCoachesOnly: Boolean(body['newCoachesOnly']),
      oneUsePerCoach: Boolean(body['oneUsePerCoach']),
      status: 'Active',
      createdAt: new Date().toISOString(),
    };
    promotions.unshift(created);
    return json(route, created, 201);
  });

  return harness;
}

trainerTest.describe('admin promotions', () => {
  trainerTest('the list shows each promotion with how, value, length, window, plans, uses and status', async ({ page }) => {
    await mockAdminPromotions(page);
    await page.goto('/admin/promotions');

    await expect(page.getByRole('heading', { name: 'Promotions', level: 1 })).toBeVisible();
    for (const header of ['Promotion', 'How', 'Value', 'Lasts', 'Offer window', 'Plans', 'Used', 'Status']) {
      await expect(page.getByRole('columnheader', { name: header })).toBeVisible();
    }

    const launch = page.getByRole('row', { name: /Launch offer/ });
    await expect(launch.getByText('For everyone at checkout')).toBeVisible();
    await expect(launch.getByText('Automatic', { exact: true })).toBeVisible();
    await expect(launch.getByText('−30 %')).toBeVisible();
    await expect(launch.getByText('3 months')).toBeVisible();
    await expect(launch.getByText('All paid plans')).toBeVisible();
    await expect(launch.getByText('Active', { exact: true })).toBeVisible();

    const partner = page.getByRole('row', { name: /Partner code/ });
    await expect(partner.getByText('COACH20')).toBeVisible();
    await expect(partner.getByText('Forever')).toBeVisible();
    await expect(partner.getByText('No end date')).toBeVisible();
    await expect(partner.getByText('18 of 50')).toBeVisible();

    const summer = page.getByRole('row', { name: /Summer 2026/ });
    await expect(summer.getByText('Start and Pro')).toBeVisible();
    await expect(summer.getByText('Ended', { exact: true })).toBeVisible();

    // No row menu; a row that has not ended carries a direct End button, an ended row none.
    await expect(page.getByRole('button', { name: /Open menu/ })).toHaveCount(0);
    await expect(launch.getByRole('button', { name: 'End Launch offer' })).toHaveAttribute('title', 'End promotion');
    await expect(page.getByRole('row', { name: /Black Friday/ }).getByRole('button', { name: 'End Black Friday' })).toBeVisible();
    await expect(summer.getByRole('button', { name: /^End / })).toHaveCount(0);
  });

  trainerTest('ending a promotion confirms, posts an empty JSON body and refetches the list', async ({ page }) => {
    const harness = await mockAdminPromotions(page);
    await page.goto('/admin/promotions');
    await expect(page.getByRole('row', { name: /Launch offer/ })).toBeVisible();
    const readsBefore = harness.listStatuses.length;

    await page.getByRole('button', { name: 'End Launch offer' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByRole('heading', { name: 'End “Launch offer”?' })).toBeVisible();
    await expect(dialog.getByText('The 12 coaches who already have it keep their discount until it runs out.')).toBeVisible();
    expect(harness.ended).toHaveLength(0);

    await dialog.getByRole('button', { name: 'End promotion' }).click();

    await expect(toastTitle(page, 'Promotion ended.')).toBeVisible();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(harness.ended).toHaveLength(1);
    expect(harness.ended[0]?.path).toBe('/admin/promotions/00000000-0000-4000-8000-000000000001/end');
    expect(harness.ended[0]?.contentType).toContain('application/json');
    expect(harness.ended[0]?.body).toBe('{}');

    // The refetch shows the row as Ended, and an Ended row has no End button.
    const launch = page.getByRole('row', { name: /Launch offer/ });
    await expect(launch.getByText('Ended', { exact: true })).toBeVisible();
    await expect(launch.getByRole('button', { name: /^End / })).toHaveCount(0);
    expect(harness.listStatuses.length).toBeGreaterThan(readsBefore);
  });

  trainerTest('the note is hidden when nobody has the promotion yet, and Cancel ends nothing', async ({ page }) => {
    const harness = await mockAdminPromotions(page);
    await page.goto('/admin/promotions');

    await page.getByRole('button', { name: 'End Black Friday' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByRole('heading', { name: 'End “Black Friday”?' })).toBeVisible();
    await expect(dialog.getByText(/already have it|already has it/)).toHaveCount(0);

    await dialog.getByRole('button', { name: 'Cancel' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(harness.ended).toHaveLength(0);
  });

  trainerTest('a promotion that already ended shows a toast and refetches', async ({ page }) => {
    const harness = await mockAdminPromotions(page, null, {
      status: 409,
      body: { status: 409, title: 'Conflict', errorCode: 'PROMOTION_ALREADY_ENDED' },
    });
    await page.goto('/admin/promotions');
    const readsBefore = harness.listStatuses.length;

    await page.getByRole('button', { name: 'End Partner code' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'End promotion' }).click();

    await expect(toastTitle(page, 'This promotion has already ended.')).toBeVisible();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect.poll(() => harness.listStatuses.length).toBeGreaterThan(readsBefore);
  });

  trainerTest('a payment-provider failure keeps the dialog open and the row unchanged', async ({ page }) => {
    const harness = await mockAdminPromotions(page, null, {
      status: 502,
      body: { status: 502, title: 'Bad Gateway', errorCode: 'BILLING_PROVIDER_ERROR' },
    });
    await page.goto('/admin/promotions');
    await expect(page.getByRole('row', { name: /Partner code/ })).toBeVisible();
    const readsBefore = harness.listStatuses.length;

    await page.getByRole('button', { name: 'End Partner code' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'End promotion' }).click();

    await expect(toastTitle(page, "The payment provider didn't answer. Please try again in a moment.")).toBeVisible();
    await expect(page.getByRole('dialog')).toBeVisible();
    expect(harness.listStatuses.length).toBe(readsBefore);
    await expect(page.getByRole('row', { name: /Partner code/ }).getByText('Active', { exact: true })).toBeVisible();
  });

  trainerTest('the tabs ask the server for that status and show only its promotions', async ({ page }) => {
    const harness = await mockAdminPromotions(page);
    await page.goto('/admin/promotions');
    await expect(page.getByRole('row', { name: /Launch offer/ })).toBeVisible();

    await page.getByRole('button', { name: 'Scheduled' }).click();
    await expect(page.getByRole('row', { name: /Black Friday/ })).toBeVisible();
    await expect(page.getByRole('row', { name: /Launch offer/ })).toHaveCount(0);

    await page.getByRole('button', { name: 'Ended' }).click();
    await expect(page.getByRole('row', { name: /Summer 2026/ })).toBeVisible();
    await expect(page.getByRole('row', { name: /Black Friday/ })).toHaveCount(0);

    await page.getByRole('button', { name: 'All' }).click();
    await expect(page.getByRole('row', { name: /Launch offer/ })).toBeVisible();

    expect(harness.listStatuses).toEqual(expect.arrayContaining(['All', 'Scheduled', 'Ended']));
  });

  trainerTest('the drawer creates a coded promotion; the code field shows only for a code', async ({ page }) => {
    const harness = await mockAdminPromotions(page);
    await page.goto('/admin/promotions');
    await page.getByRole('button', { name: 'New promotion' }).click();

    const drawer = page.getByRole('dialog');
    await expect(drawer.getByRole('heading', { name: 'New promotion' })).toBeVisible();
    await expect(drawer.getByLabel('Code', { exact: true })).toHaveCount(0);

    await drawer.getByLabel('Name', { exact: true }).fill('Spring offer');
    await drawer.getByLabel('Value', { exact: true }).fill('25');

    await drawer.getByRole('button', { name: 'With a code' }).click();
    await drawer.getByLabel('Code', { exact: true }).fill('SPRING25');
    await drawer.getByRole('button', { name: 'Automatic for everyone' }).click();
    await expect(drawer.getByLabel('Code', { exact: true })).toHaveCount(0);
    await drawer.getByRole('button', { name: 'With a code' }).click();
    await drawer.getByLabel('Code', { exact: true }).fill('SPRING25');

    // The month count shows only for "Several months".
    await expect(drawer.getByLabel('Number of months')).toHaveCount(0);
    await drawer.getByRole('button', { name: 'Several months' }).click();
    await drawer.getByLabel('Number of months').fill('3');

    await drawer.getByRole('button', { name: 'Save promotion' }).click();

    await expect(toastTitle(page, 'Promotion saved.')).toBeVisible();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(page.getByRole('row', { name: /Spring offer/ })).toBeVisible();

    expect(harness.created).toHaveLength(1);
    expect(harness.created[0]).toMatchObject({
      name: 'Spring offer',
      kind: 'Percent',
      value: 25,
      mode: 'Code',
      code: 'SPRING25',
      duration: 'Months',
      durationMonths: 3,
      planCodes: ['start', 'pro', 'studio'],
      newCoachesOnly: false,
      oneUsePerCoach: true,
    });
    expect(typeof harness.created[0]?.['claimFrom']).toBe('string');
  });

  trainerTest('a fixed amount is sent in minor units and blocks an empty name before any request', async ({ page }) => {
    const harness = await mockAdminPromotions(page);
    await page.goto('/admin/promotions');
    await page.getByRole('button', { name: 'New promotion' }).click();

    const drawer = page.getByRole('dialog');
    await drawer.getByRole('button', { name: 'Save promotion' }).click();
    await expect(drawer.getByText('Enter a name.')).toBeVisible();
    expect(harness.created).toHaveLength(0);

    await drawer.getByLabel('Name', { exact: true }).fill('Crown off');
    await drawer.getByRole('button', { name: 'Fixed amount' }).click();
    await drawer.getByLabel('Value', { exact: true }).fill('200');
    await drawer.getByRole('button', { name: 'Save promotion' }).click();

    await expect(toastTitle(page, 'Promotion saved.')).toBeVisible();
    expect(harness.created[0]).toMatchObject({ kind: 'FixedAmount', value: 20000, currency: 'CZK', mode: 'Automatic' });
  });

  trainerTest('a code already in use is flagged on the Code field and the drawer stays open', async ({ page }) => {
    await mockAdminPromotions(page, {
      status: 409,
      body: { status: 409, title: 'Conflict', errorCode: 'PROMOTION_CODE_TAKEN' },
    });
    await page.goto('/admin/promotions');
    await page.getByRole('button', { name: 'New promotion' }).click();

    const drawer = page.getByRole('dialog');
    await drawer.getByLabel('Name', { exact: true }).fill('Partner again');
    await drawer.getByLabel('Value', { exact: true }).fill('10');
    await drawer.getByRole('button', { name: 'With a code' }).click();
    await drawer.getByLabel('Code', { exact: true }).fill('COACH20');
    await drawer.getByRole('button', { name: 'Save promotion' }).click();

    await expect(drawer.getByText('Another promotion that has not ended already uses this code.')).toBeVisible();
    await expect(drawer.getByLabel('Name', { exact: true })).toHaveValue('Partner again');
  });
});
