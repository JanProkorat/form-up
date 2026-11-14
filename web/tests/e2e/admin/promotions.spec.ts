/**
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

function json(route: Route, body: unknown, status = 200, contentType = 'application/json') {
  return route.fulfill({ status, contentType, body: JSON.stringify(body) });
}

interface Harness {
  /** The status query value of every list read, in order. */
  listStatuses: string[];
  /** The JSON body of every create request, in order. */
  created: Record<string, unknown>[];
}

/**
 * Mocks the admin reads and the create. `createOutcome` lets a test turn the create into a refusal; a successful
 * create is added to the list, so the refetch after it shows the new row.
 */
async function mockAdminPromotions(
  page: Page,
  createOutcome: { status: number; body: unknown } | null = null,
): Promise<Harness> {
  const harness: Harness = { listStatuses: [], created: [] };
  const promotions = [...PROMOTIONS];

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

    // A promotion has no edit or end action yet, so a row carries no menu button.
    await expect(page.getByRole('button', { name: /Open menu/ })).toHaveCount(0);
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

    await expect(page.getByText('Promotion saved.')).toBeVisible();
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

    await expect(page.getByText('Promotion saved.')).toBeVisible();
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
