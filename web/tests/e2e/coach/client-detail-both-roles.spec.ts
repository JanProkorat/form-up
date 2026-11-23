/**
 * Client detail overview for a both-roles link (qa.coach -> "QA Duo", Trainer +
 * Nutritionist, both capabilities): a Nutrition tab (no Workouts tab), a Meal
 * compliance / PRs this month / Client since stat row, both the meal plan and
 * latest workout cards, and a profile with injuries, allergies, diet, training
 * days and daily target rows. Training compliance and Weight change are not
 * shown. Run against a freshly reset harness (docs/testing/e2e-fixtures.md,
 * "Seeded both-roles coach (#1311)").
 */
import { coachTest as test, expect } from '../fixtures/auth';

test.describe('client detail — both-roles overview', () => {
  test('renders the both-roles layout', async ({ page }) => {
    await page.goto('/clients');
    await page.waitForLoadState('networkidle');

    const row = page.getByRole('row', { name: /QA Duo/ });
    await expect(row).toBeVisible();
    await row.getByRole('link', { name: /QA Duo/ }).click();
    await page.waitForLoadState('networkidle');

    await expect(page.getByRole('heading', { name: /QA Duo/ })).toBeVisible();

    // Tabs: Nutrition is live, there is no Workouts tab.
    await expect(page.getByRole('tab', { name: 'Overview' })).toBeEnabled();
    await expect(page.getByRole('tab', { name: 'Nutrition' })).toBeEnabled();
    await expect(page.getByRole('tab', { name: 'Workouts' })).toHaveCount(0);

    // Stat row: Meal compliance, PRs this month (1), Client since.
    for (const label of ['Meal compliance', 'PRs this month', 'Client since']) {
      await expect(page.getByText(label, { exact: true })).toBeVisible();
    }
    for (const label of ['Training compliance', 'Weight change']) {
      await expect(page.getByText(label, { exact: true })).toHaveCount(0);
    }

    // Meal compliance uses the nutrition-only figures (13 of 24 planned meals, streak 6),
    // not the combined 56 % / 7. Holds when the spec runs the same UTC day as /test/reset.
    const mealTile = page.getByText('Meal compliance', { exact: true }).locator('xpath=..');
    await expect(mealTile).toContainText('54 %');
    await expect(mealTile).toContainText('6-day logging streak');

    const prTile = page.getByText('PRs this month', { exact: true }).locator('xpath=..');
    await expect(prTile).toContainText('1');

    // Both the meal plan and the latest workout cards.
    await expect(page.getByRole('region', { name: 'Current meal plan' })).toBeVisible();
    await expect(page.getByRole('region', { name: 'Latest workout' })).toBeVisible();
    await expect(page.getByText('No training access')).toHaveCount(0);
    await expect(page.getByText('No nutrition access')).toHaveCount(0);

    // Client profile: injuries and allergies highlights plus diet, training
    // days and daily target rows.
    for (const label of ['Injuries', 'Allergies', 'Diet style', 'Training days', 'Daily target']) {
      await expect(page.getByText(label, { exact: true })).toBeVisible();
    }

    // Check-in trend and weight stay for every role.
    for (const name of ['Check-in trend', 'Weight', 'Recent activity', 'Client profile']) {
      await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
    }
  });
});
