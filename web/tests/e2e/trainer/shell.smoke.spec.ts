/**
 * Durable spec — v1 app shell smoke test (#1051).
 *
 * Proves the phase-0 foundation end-to-end against the compose harness:
 *   1. API-driven auth (auth.setup.ts / trainerTest) reaches a protected
 *      route without ProtectedRoute looping back to "/".
 *   2. The app shell renders (sidebar with the v1 nav, top bar).
 *   3. Client-side routing between nav items actually swaps page content.
 *
 * Runs under the `trainer` project, authenticated via the `trainerTest`
 * export in ../fixtures/auth.ts (#897 — mints a fresh per-attempt refresh
 * token rather than reusing the shared .auth/trainer.json token).
 */
import { trainerTest as test, expect } from '../fixtures/auth';

test('app shell renders the v1 nav and routes between pages', async ({ page }) => {
  await page.goto('/clients');

  // Wait for restoreSession() (POST /auth/refresh on mount) to complete
  // before asserting page content.
  await page.waitForLoadState('networkidle');

  // Confirm ProtectedRoute did not bounce us back to the public entry route.
  await expect(page).toHaveURL(/\/clients$/);

  // A trainer-only coach sees the daily-work items and no Recipes, Ingredients
  // or Plan templates — those are gated on the active Nutritionist role (#1325; the
  // nutritionist side is covered by nutritionist/shell.role-nav.spec.ts).
  const nav = page.getByRole('navigation');
  await expect(nav.getByText('Clients', { exact: true })).toBeVisible();
  await expect(nav.getByText('Inbox', { exact: true })).toBeVisible();
  await expect(nav.getByText('Ingredients', { exact: true })).toHaveCount(0);
  await expect(nav.getByText('Recipes', { exact: true })).toHaveCount(0);
  await expect(nav.getByText('Plan templates', { exact: true })).toHaveCount(0);

  // The sidebar user card opens the account menu, which holds the sign-out control.
  await page.getByRole('button', { name: 'Account menu' }).click();
  await expect(page.getByRole('button', { name: 'Log out' })).toBeVisible();
  await page.keyboard.press('Escape');

  // Routing: clicking a different nav item swaps the page heading.
  await expect(page.getByRole('heading', { name: 'Clients' })).toBeVisible();
  await nav.getByText('Inbox', { exact: true }).click();
  await expect(page).toHaveURL(/\/inbox$/);
  await expect(page.getByRole('heading', { name: 'Inbox' })).toBeVisible();
});

test('sidebar shows the Notifications row above Help and collapses to a rail that survives reload', async ({
  page,
}) => {
  await page.goto('/clients');
  await page.waitForLoadState('networkidle');

  const aside = page.locator('aside');
  const nav = page.getByRole('navigation');

  // Bell lives in a row directly above Help & Support and is a live control that opens the popover.
  const notifications = nav.getByRole('button', { name: /^Notifications/ });
  await expect(notifications).toBeVisible();
  await expect(notifications).toBeEnabled();
  await expect(aside).toHaveCSS('width', '248px');

  // Collapse: 64px rail, labels out of reach, focus stays on the (now Expand) toggle.
  await nav.getByRole('button', { name: 'Collapse navigation' }).click();
  await expect(aside).toHaveCSS('width', '64px');
  const expand = nav.getByRole('button', { name: 'Expand navigation' });
  await expect(expand).toBeFocused();
  await expect(nav.getByRole('link', { name: 'Clients' })).toBeVisible();
  await expect(nav.getByRole('button', { name: 'Help & Support' })).toHaveCount(0);
  await expect(nav.getByRole('button', { name: /^Notifications/ })).toBeVisible();

  // The choice is remembered per browser.
  await page.reload();
  await page.waitForLoadState('networkidle');
  await expect(aside).toHaveCSS('width', '64px');

  // Expand again: full sidebar, focus on the Collapse toggle.
  await nav.getByRole('button', { name: 'Expand navigation' }).click();
  await expect(aside).toHaveCSS('width', '248px');
  await expect(nav.getByRole('button', { name: 'Collapse navigation' })).toBeFocused();
  await page.reload();
  await page.waitForLoadState('networkidle');
  await expect(aside).toHaveCSS('width', '248px');
});


