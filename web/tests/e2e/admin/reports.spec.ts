/**
 * Admin Reports: an admin reaches the page from the sidebar and sees the Money, Coaches and Usage sections, and the
 * period switch asks the server for that window; a non-admin neither sees the entry nor reaches the route.
 *
 * The e2e fixture has no Admin account and no daily snapshots, so the spec signs in as the QA trainer, lets the roles
 * read report Admin (the guard and the sidebar read Admin from there) and mocks GET /admin/reports. The figures and
 * percentages are asserted in the component test; this spec only covers navigation, access and the request.
 */
import type { Page, Route } from '@playwright/test';
import { trainerTest, expect } from '../fixtures/auth';

function json(route: Route, body: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

const REPORT = {
  period: 'Days30',
  snapshotDay: '2026-11-15',
  money: {
    monthlyRevenueFull: [{ currency: 'CZK', value: 2648000, previous: 2400000 }],
    monthlyRevenueAfterPromotions: [{ currency: 'CZK', value: 2186000, previous: 2020000 }],
    averagePerPayingCoach: [{ currency: 'CZK', value: 59100, previous: 57900, coachesBilled: 37 }],
    collectedThisMonth: [{ currency: 'CZK', value: 1994000, previous: 1872000 }],
    lostToCancellations: { count: { value: 3, previous: 2 }, amounts: [{ currency: 'CZK', value: 119600 }] },
    churnPercent: 7.5,
    plans: [{ planCode: 'start', nameCs: 'Start', nameEn: 'Start', nameDe: 'Start', payingCoaches: 18, revenue: [] }],
    addOns: [],
  },
  funnel: { signedUp: 64, emailVerified: 58, chosePlan: 51, startedPaidPlan: 9 },
  planChanges: { paymentFailedCoaches: 2, promotionCodesApplied: 6 },
  mrrSeries: [
    {
      day: '2026-11-01',
      full: [{ currency: 'CZK', amountMinorUnits: 2400000 }],
      afterPromotions: [{ currency: 'CZK', amountMinorUnits: 2020000 }],
    },
    {
      day: '2026-11-15',
      full: [{ currency: 'CZK', amountMinorUnits: 2648000 }],
      afterPromotions: [{ currency: 'CZK', amountMinorUnits: 2186000 }],
    },
  ],
  usage: {
    totalCoaches: 87,
    activeCoachesLast30Days: 61,
    activeClientsLast7Days: { value: 412, previous: 392 },
    planWeeksPublishedLast7Days: 138,
    freeCoachesAtClientCap: 12,
    medianClientsPerCoach: 6,
  },
};

/** Mocks GET /admin/reports and records the `period` query of every read. */
async function mockReports(page: Page): Promise<string[]> {
  const periods: string[] = [];
  await page.route(/\/admin\/reports(\?.*)?$/, (route) => {
    // The page navigation to /admin/business-reports is not an API call; only fetch and XHR are mocked.
    const resourceType = route.request().resourceType();
    if (resourceType !== 'fetch' && resourceType !== 'xhr') {
      return route.fallback();
    }
    const period = new URL(route.request().url()).searchParams.get('period') ?? '';
    periods.push(period);
    return json(route, { ...REPORT, period });
  });
  return periods;
}

async function mockAdminRole(page: Page) {
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
}

trainerTest.describe('admin reports', () => {
  trainerTest('an admin opens Reports from the sidebar and the switch asks for the chosen period', async ({ page }) => {
    await mockAdminRole(page);
    const periods = await mockReports(page);
    await page.goto('/clients');

    await page.getByRole('link', { name: 'Reports', exact: true }).click();

    await expect(page).toHaveURL(/\/admin\/business-reports$/);
    await expect(page.getByRole('heading', { name: 'Reports', level: 1 })).toBeVisible();
    for (const section of ['Money', 'Coaches', 'Usage']) {
      await expect(page.getByRole('heading', { name: section, level: 2 })).toBeVisible();
    }
    expect(periods).toEqual(['Days30']);

    const request = page.waitForRequest((req) => req.url().includes('/admin/reports?period=Days90'));
    await page.getByRole('button', { name: '90 days' }).click();
    await request;
    await expect(page.getByRole('button', { name: '90 days' })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('heading', { name: 'Money', level: 2 })).toBeVisible();
  });

  trainerTest('a non-admin has no Reports entry and cannot reach the route', async ({ page }) => {
    const periods = await mockReports(page);
    await page.goto('/clients');
    await expect(page.getByRole('link', { name: /^Clients/ })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Reports', exact: true })).toHaveCount(0);

    await page.goto('/admin/business-reports');

    await expect(page).not.toHaveURL(/business-reports/);
    await expect(page.getByRole('heading', { name: 'Reports', level: 1 })).toHaveCount(0);
    expect(periods).toEqual([]);
  });
});
