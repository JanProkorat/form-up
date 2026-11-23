/**
 * #1073 — app shell responsive behaviour, across all four v1 routes.
 *
 * shell.smoke.spec.ts (out of scope for #1073, must pass unmodified) already
 * proves the desktop shell renders and routes; this spec covers what that
 * one doesn't:
 *   1. All four v1 routes — not just /clients and /inbox — at desktop width.
 *   2. No route scrolls horizontally at ~390px (#1066 regression guard).
 *   3. The off-canvas drawer, opened from the hamburger trigger that now
 *      lives inside <main> (the top bar it used to live in is gone), still
 *      exposes the nav and a reachable logout control.
 *
 * Below `lg`, the static sidebar stays mounted (`hidden lg:flex` is
 * display:none, not unmount) while the drawer's own Sidebar copy renders
 * once open — see docs/design/1073/shell-inventory.md #6. An unscoped
 * `getByText` would resolve two matches once both exist, so every assertion
 * inside the drawer test below is scoped to `page.getByRole('dialog')`.
 */
import type { Locator, Page } from '@playwright/test';
import { trainerTest as test, expect } from '../fixtures/auth';

interface ExitRecord {
  closedWhileConnected: boolean;
  closedAnimationName: string;
}

type RecorderWindow = Window & { __sheetExit?: Record<string, ExitRecord> };

/**
 * Records, inside the page, whether the element was seen with
 * `data-state="closed"` while still connected, and its computed
 * `animation-name` at that moment (same approach as overlay-primitives.spec.ts).
 */
async function recordExit(locator: Locator, key: string): Promise<void> {
  await locator.evaluate((element, recorderKey) => {
    const recorderWindow = window as RecorderWindow;
    const records = (recorderWindow.__sheetExit ??= {});
    records[recorderKey] = { closedWhileConnected: false, closedAnimationName: '' };
    new MutationObserver(() => {
      if (element.getAttribute('data-state') === 'closed' && element.isConnected) {
        records[recorderKey] = {
          closedWhileConnected: true,
          closedAnimationName: getComputedStyle(element).animationName,
        };
      }
    }).observe(element, { attributes: true, attributeFilter: ['data-state'] });
  }, key);
}

const readExit = (page: Page, key: string): Promise<ExitRecord | undefined> =>
  page.evaluate((recorderKey) => (window as RecorderWindow).__sheetExit?.[recorderKey], key);

const ROUTES = [
  { path: '/clients', heading: 'Clients' },
  { path: '/inbox', heading: 'Inbox' },
];

test.describe('app shell responsive behaviour', () => {
  test('a trainer-only coach is redirected from /recipes to /clients (#1325)', async ({ page }) => {
    await page.goto('/recipes');
    await page.waitForLoadState('networkidle');
    await expect(page).toHaveURL(/\/clients$/);
    await expect(page.getByRole('heading', { name: 'Clients' })).toBeVisible();
  });

  test.describe('desktop', () => {
    for (const { path, heading } of ROUTES) {
      test(`${path} renders the static sidebar and page heading`, async ({ page }) => {
        await page.goto(path);
        await page.waitForLoadState('networkidle');

        await expect(page.getByRole('heading', { name: heading })).toBeVisible();

        const nav = page.getByRole('navigation');
        await expect(nav.getByText('Clients', { exact: true })).toBeVisible();
        await expect(nav.getByText('Inbox', { exact: true })).toBeVisible();
        // The nutrition library items belong to the Nutritionist role (#1325).
        await expect(nav.getByText('Recipes', { exact: true })).toHaveCount(0);
        await expect(nav.getByText('Ingredients', { exact: true })).toHaveCount(0);
        await expect(nav.getByText('Plan templates', { exact: true })).toHaveCount(0);
      });
    }
  });

  for (const width of [1024, 1440]) {
    test.describe(`${width}px`, () => {
      test.use({ viewport: { width, height: 900 } });

      const measure = (page: import('@playwright/test').Page) =>
        page.evaluate(() => {
          const main = document.querySelector('main');
          return {
            pageScroll: document.documentElement.scrollWidth - window.innerWidth,
            mainScroll: main ? main.scrollWidth - main.clientWidth : 0,
          };
        });

      for (const { path } of ROUTES) {
        test(`${path} has no horizontal overflow`, async ({ page }) => {
          await page.goto(path);
          await page.waitForLoadState('networkidle');

          const { pageScroll, mainScroll } = await measure(page);
          expect(pageScroll).toBeLessThanOrEqual(0);
          expect(mainScroll).toBeLessThanOrEqual(0);
        });
      }

      test('a client detail page has no horizontal overflow', async ({ page }) => {
        await page.goto('/clients');
        await page.waitForLoadState('networkidle');
        await page.locator('a[href^="/clients/"]').first().click();
        await page.waitForLoadState('networkidle');

        const { pageScroll, mainScroll } = await measure(page);
        expect(pageScroll).toBeLessThanOrEqual(0);
        expect(mainScroll).toBeLessThanOrEqual(0);
      });
    });
  }

  test.describe('390px', () => {
    test.use({ viewport: { width: 390, height: 844 } });

    for (const { path } of ROUTES) {
      test(`${path} has no horizontal overflow`, async ({ page }) => {
        await page.goto(path);
        await page.waitForLoadState('networkidle');

        const { scrollWidth, innerWidth } = await page.evaluate(() => ({
          scrollWidth: document.documentElement.scrollWidth,
          innerWidth: window.innerWidth,
        }));

        expect(scrollWidth).toBeLessThanOrEqual(innerWidth);
      });
    }

    test('the off-canvas drawer opens from the hamburger and exposes the nav plus logout', async ({
      page,
    }) => {
      await page.goto('/clients');
      await page.waitForLoadState('networkidle');

      await page.getByRole('button', { name: 'Open navigation' }).click();

      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible();
      await expect(dialog.getByText('Clients', { exact: true })).toBeVisible();
      await expect(dialog.getByText('Inbox', { exact: true })).toBeVisible();
      await expect(dialog.getByText('Recipes', { exact: true })).toHaveCount(0);
      await expect(dialog.getByText('Ingredients', { exact: true })).toHaveCount(0);
      await expect(dialog.getByText('Plan templates', { exact: true })).toHaveCount(0);
      await expect(dialog.getByRole('button', { name: 'Notifications' })).toBeVisible();
      // The drawer is always full width: no collapse toggle.
      await expect(dialog.getByRole('button', { name: 'Collapse navigation' })).toHaveCount(0);
      await expect(dialog.getByRole('button', { name: 'Expand navigation' })).toHaveCount(0);
      // Log out lives inside the account menu.
      await dialog.getByRole('button', { name: 'Account menu' }).click();
      await expect(page.getByRole('button', { name: 'Log out' })).toBeVisible();
    });

    /**
     * #1081 — the drawer's slide/fade was expressed as a CSS *transition*,
     * which Radix's Presence (@radix-ui/react-presence) can't animate: a
     * transition has no `animationName`, so `getAnimationName()` returns
     * "none" and Presence unmounts the element on the SAME tick the close
     * is triggered — open never animated either, because a transition
     * needs a previous value and Radix mounts the element already in its
     * final state. Fixed by expressing both states as `@keyframes`, with a
     * DISTINCT keyframe name per direction (Presence gates the exit on
     * `prevAnimationName !== currentAnimationName`, so a single keyframe
     * reused via `animation-direction: reverse` still computes to the same
     * name and still unmounts instantly).
     */
    test('the drawer content and overlay animate with distinct enter/exit keyframes and suspend removal on close (#1081)', async ({
      page,
    }) => {
      await page.goto('/clients');
      await page.waitForLoadState('networkidle');

      await page.getByRole('button', { name: 'Open navigation' }).click();

      const dialog = page.getByRole('dialog');
      const overlay = page.locator("[data-slot='sheet-overlay']");
      await expect(dialog).toBeVisible();

      // A. CSS contract. `animation-name` persists in computed style after
      // the animation ends, so this is not a transient assertion.
      await expect(dialog).toHaveCSS('animation-name', 'sheet-in-left');
      await expect(overlay).toHaveCSS('animation-name', 'sheet-overlay-in');
      const enterContentName = await dialog.evaluate((el) => getComputedStyle(el).animationName);
      const enterOverlayName = await overlay.evaluate((el) => getComputedStyle(el).animationName);

      // The exit animations last 200ms, shorter than a few sequential
      // Playwright round trips under load, so record the closed state inside
      // the page instead of polling for it after the click.
      await recordExit(dialog, 'content');
      await recordExit(overlay, 'overlay');

      await dialog.getByRole('button', { name: 'Close' }).click();

      // B. Exit suspension — the actual regression test. Before the fix
      // the element was already gone (unmounted on the same tick as the
      // click), so it was never seen with data-state="closed" while still
      // connected: there was no real CSS animation to suspend the removal on.
      await expect
        .poll(() => readExit(page, 'content'))
        .toMatchObject({ closedWhileConnected: true, closedAnimationName: 'sheet-out-left' });
      await expect
        .poll(() => readExit(page, 'overlay'))
        .toMatchObject({ closedWhileConnected: true, closedAnimationName: 'sheet-overlay-out' });
      const contentExit = await readExit(page, 'content');
      const overlayExit = await readExit(page, 'overlay');
      expect(contentExit?.closedAnimationName).not.toBe(enterContentName);
      expect(overlayExit?.closedAnimationName).not.toBe(enterOverlayName);

      await expect(dialog).toBeHidden();
    });
  });
});
