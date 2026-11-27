/**
 * #1325 — the nutrition Library items of the shell belong to the active
 * Nutritionist role. The trainer-only side (no such items, /recipes redirects)
 * lives in trainer/shell.smoke.spec.ts and trainer/shell.responsive.spec.ts;
 * this spec holds the nutritionist side, including the nutrition routes'
 * desktop render and overflow checks that used to run as the trainer.
 */
import { nutritionistTest as test, expect } from '../fixtures/auth';

const NUTRITION_ROUTES = [
  { path: '/recipes', heading: 'Recipes' },
  { path: '/ingredients', heading: 'Ingredients' },
  { path: '/plan-templates', heading: 'Plan templates' },
];

test.describe('nutritionist shell', () => {
  test('sidebar shows the client items and the nutrition Library items', async ({ page }) => {
    await page.goto('/clients');
    await page.waitForLoadState('networkidle');

    const nav = page.getByRole('navigation');
    await expect(nav.getByText('Clients', { exact: true })).toBeVisible();
    await expect(nav.getByText('Inbox', { exact: true })).toBeVisible();
    await expect(nav.getByText('Ingredients', { exact: true })).toBeVisible();
    await expect(nav.getByText('Recipes', { exact: true })).toBeVisible();
    await expect(nav.getByText('Plan templates', { exact: true })).toBeVisible();
  });

  for (const { path, heading } of NUTRITION_ROUTES) {
    test(`${path} renders the page without a redirect`, async ({ page }) => {
      await page.goto(path);
      await page.waitForLoadState('networkidle');

      await expect(page).toHaveURL(new RegExp(`${path}$`));
      await expect(page.getByRole('heading', { name: heading })).toBeVisible();
    });
  }

  for (const width of [1024, 1440, 390]) {
    test.describe(`${width}px`, () => {
      test.use({ viewport: { width, height: 900 } });

      for (const { path } of NUTRITION_ROUTES) {
        test(`${path} has no horizontal overflow`, async ({ page }) => {
          await page.goto(path);
          await page.waitForLoadState('networkidle');

          const overflow = await page.evaluate(() => {
            const main = document.querySelector('main');
            return {
              pageScroll: document.documentElement.scrollWidth - window.innerWidth,
              mainScroll: main ? main.scrollWidth - main.clientWidth : 0,
            };
          });
          expect(overflow.pageScroll).toBeLessThanOrEqual(0);
          expect(overflow.mainScroll).toBeLessThanOrEqual(0);
        });
      }
    });
  }
});
