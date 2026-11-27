/**
 * Admin-only access (#2088): an account that is Admin and nothing else sees just the libraries and the Admin section
 * in the sidebar, and every other portal route answers with the 403 page.
 *
 * The e2e fixture has no Admin-only account, so the spec signs in as the QA trainer and rewrites the roles of
 * GET /users/me (the session restore reads the roles from there) to Admin alone. A token without a coach role makes
 * the app skip the coach lookups, so the only requests mocked besides the profile are the plans list and the two
 * blocks of the Overview, the landing page. Entry-by-role, the guard and the request lists are asserted in the
 * component tests.
 */
import type { Page, Route } from '@playwright/test';
import { trainerTest, expect } from '../fixtures/auth';

function json(route: Route, body: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

async function signInAsAdminOnly(page: Page) {
  await page.route(/\/users\/me$/, async (route) => {
    if (route.request().method() !== 'GET') {
      return route.fallback();
    }
    const profile = (await (await route.fetch()).json()) as Record<string, unknown>;
    return json(route, { ...profile, roles: ['Admin'] });
  });
  await page.route(/\/admin\/subscription-plans$/, (route) => {
    const resourceType = route.request().resourceType();
    if (resourceType !== 'fetch' && resourceType !== 'xhr') {
      return route.fallback();
    }
    return json(route, { plans: [] });
  });
  await page.route(/\/admin\/overview\/attention$/, (route) =>
    json(route, { paymentFailedCount: 0, cancellingCount: 0, givenFreeEndingSoonCount: 0, promotionsEndingSoon: [] }),
  );
  await page.route(/\/admin\/reports(\?.*)?$/, (route) => json(route, { period: 'Days30', money: {}, funnel: {} }));
}

trainerTest.describe('admin-only access', () => {
  trainerTest('the sidebar offers only the libraries and the Admin section', async ({ page }) => {
    await signInAsAdminOnly(page);
    await page.goto('/admin/plans');

    for (const name of ['Recipes', 'Ingredients', 'Overview', 'Plans', 'Promotions', 'Coaches', 'Reports']) {
      await expect(page.getByRole('link', { name, exact: true })).toBeVisible();
    }
    for (const name of [/^Clients/, /^Inbox/, /^Forms/, /^Packages/, /^Plan templates/]) {
      await expect(page.getByRole('link', { name })).toHaveCount(0);
    }

    await page.getByRole('button', { name: 'Account menu' }).click();
    await expect(page.getByRole('button', { name: 'Log out' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Your profile' })).toHaveCount(0);
    await expect(page.getByRole('link', { name: 'Settings', exact: true })).toHaveCount(0);
  });

  trainerTest('signing in lands on the Overview, not on a coach page', async ({ page }) => {
    await signInAsAdminOnly(page);
    await page.goto('/login');

    await expect(page).toHaveURL(/\/admin$/);
    await expect(page.getByRole('heading', { name: 'Overview', level: 1 })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Overview', exact: true })).toHaveAttribute('aria-current', 'page');
  });

  trainerTest('a coach route shows the 403 page with a way back to the Admin section', async ({ page }) => {
    await signInAsAdminOnly(page);
    await page.goto('/clients');

    await expect(page.getByRole('heading', { name: 'No access to this page' })).toBeVisible();
    await expect(page).toHaveURL(/\/clients$/);

    await page.getByRole('link', { name: 'Back to Admin' }).click();

    await expect(page).toHaveURL(/\/admin$/);
  });
});
