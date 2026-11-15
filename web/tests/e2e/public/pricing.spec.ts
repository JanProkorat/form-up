/**
 * Public pricing page — unauthenticated visitor flow.
 *
 * Runs under the `public` project (playwright.config.ts): no `setup` dependency and no
 * `storageState`, so the browser context starts signed out. Plans are admin-editable, so
 * nothing here asserts a plan name or a price.
 */
import { test, expect } from '@playwright/test';

const PLANS_ENDPOINT = '**/subscription-plans';

// English keeps the text assertions stable regardless of the app's default language.
test.beforeEach(async ({ page }) => {
  await page.addInitScript("window.localStorage.setItem('lang', 'en');");
});

test('/pricing shows the heading and plan cards without redirecting', async ({ page }) => {
  await page.goto('/pricing');

  await expect(page).toHaveURL(/\/pricing$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Plans and pricing' })).toBeVisible();
  await expect(page.getByRole('article').first()).toBeVisible();
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('the landing nav Pricing link opens /pricing', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Pricing' }).click();

  await expect(page).toHaveURL(/\/pricing$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Plans and pricing' })).toBeVisible();
});

test('the compare table lists all 13 features', async ({ page }) => {
  await page.goto('/pricing');

  await expect(page.getByTestId('pricing-feature-row')).toHaveCount(13);
});

test('at most one plan carries the Most popular badge', async ({ page }) => {
  await page.goto('/pricing');
  await expect(page.getByRole('article').first()).toBeVisible();

  expect(await page.getByText('Most popular', { exact: true }).count()).toBeLessThanOrEqual(1);
});

test('a failing plans request shows an alert, and Try again recovers', async ({ page }) => {
  await page.route(PLANS_ENDPOINT, (route) => route.fulfill({ status: 500, contentType: 'application/json', body: '{}' }));
  await page.goto('/pricing');

  const alert = page.getByRole('alert');
  await expect(alert).toBeVisible();

  await page.unroute(PLANS_ENDPOINT);
  await page.route(PLANS_ENDPOINT, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ plans: [] }) }),
  );
  await alert.getByRole('button', { name: 'Try again' }).click();

  await expect(page.getByText('No plans are offered right now.')).toBeVisible();
});

test('a rate-limited plans request shows the error state', async ({ page }) => {
  await page.route(PLANS_ENDPOINT, (route) => route.fulfill({ status: 429, contentType: 'application/json', body: '{}' }));
  await page.goto('/pricing');

  await expect(page.getByRole('alert')).toBeVisible();
  await expect(page).toHaveURL(/\/pricing$/);
});

test('an empty plan list shows the empty state and no compare table', async ({ page }) => {
  await page.route(PLANS_ENDPOINT, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ plans: [] }) }),
  );
  await page.goto('/pricing');

  await expect(page.getByText('No plans are offered right now.')).toBeVisible();
  await expect(page.getByRole('table')).toHaveCount(0);
  await expect(page.getByRole('article')).toHaveCount(0);
});
