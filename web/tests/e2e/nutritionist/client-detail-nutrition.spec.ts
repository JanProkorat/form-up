/**
 * Client detail Nutrition tab for a nutritionist-only link (the QA nutritionist
 * and the seeded QA Client, who has one Active plan and no past plans).
 *
 * Only the journey against the real seeded plan lives here. The tab's empty, error and
 * edit states, request bodies and week maths are Vitest component tests under
 * src/components/client-detail/nutrition/*.test.tsx.
 * Run against a freshly reset harness.
 */
import type { Page } from '@playwright/test';
import { nutritionistTest as test, expect } from '../fixtures/auth';

async function openClientDetail(page: Page): Promise<void> {
  await page.goto('/clients');
  await page.waitForLoadState('networkidle');
  const row = page.getByRole('row', { name: /QA Client/ });
  await expect(row).toBeVisible();
  await row.getByRole('link', { name: /QA Client/ }).click();
  await page.waitForLoadState('networkidle');
  await expect(page.getByRole('heading', { name: /QA Client/ })).toBeVisible();
}

async function openNutritionTab(page: Page): Promise<void> {
  await openClientDetail(page);
  await page.getByRole('tab', { name: 'Nutrition' }).click();
  await expect(page).toHaveURL(/\/clients\/[^/]+\/nutrition$/);
}

test.describe('client detail — nutrition tab', () => {
  test('is URL-driven and shows the current plan, timeline and past plans', async ({ page }) => {
    await openNutritionTab(page);

    await expect(page.getByRole('tab', { name: 'Nutrition' })).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByRole('tab', { name: 'Overview' })).toHaveAttribute('aria-selected', 'false');

    // Current plan: the seeded two-week plan, week 2 in progress, an editor link.
    await expect(page.getByText('Current meal plan', { exact: true })).toBeVisible();
    const strip = page.getByRole('list', { name: /Weeks of the plan/ });
    await expect(strip.getByRole('listitem')).toHaveCount(2);
    await expect(strip.locator('[data-state="done"]')).toHaveCount(1);
    await expect(strip.locator('[data-state="current"]')).toHaveCount(1);
    const currentCard = page.locator('section', { has: page.getByText('Current meal plan', { exact: true }) });
    for (const legend of ['Done', 'This week', 'Published, upcoming']) {
      await expect(currentCard.getByText(legend, { exact: true })).toBeVisible();
    }
    await expect(page.getByRole('link', { name: 'Open editor' })).toHaveAttribute(
      'href',
      /\/clients\/[^/]+\/nutrition\/plans\/[^/]+$/,
    );
    await expect(
      page.getByTestId('client-header-facts').getByRole('link', { name: 'Open meal plan, week 2 of 2' }),
    ).toHaveAttribute('href', /\/clients\/[^/]+\/nutrition\/plans\/[^/]+$/);
    await expect(page.getByText('Preview as client')).toHaveCount(0);
    await expect(page.getByText(/2,500 kcal a day/)).toBeVisible();

    // Timeline: the plan is a bar that opens the editor and today is marked.
    await expect(page.getByRole('heading', { name: 'Plan timeline' })).toBeVisible();
    await expect(page.getByTestId('plan-timeline-row').getByRole('link').first()).toHaveAttribute(
      'href',
      /\/clients\/[^/]+\/nutrition\/plans\/[^/]+$/,
    );
    await expect(page.getByText('Today', { exact: true })).toBeVisible();

    // Past plans: none yet for this client.
    await expect(page.getByRole('heading', { name: 'Past plans' })).toBeVisible();
    await expect(page.getByText('none yet')).toBeVisible();

    // The tab survives a reload, and Back returns to Overview.
    await page.reload();
    await page.waitForLoadState('networkidle');
    await expect(page.getByRole('tab', { name: 'Nutrition' })).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByText('Current meal plan', { exact: true })).toBeVisible();

    await page.getByRole('tab', { name: 'Overview' }).click();
    await expect(page).toHaveURL(/\/clients\/[^/]+$/);
    await expect(page.getByRole('region', { name: 'Current meal plan' })).toBeVisible();
  });
});
