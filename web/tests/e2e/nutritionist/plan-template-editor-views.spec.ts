/**
 * Plan template editor views (epic #1237, task #1241): the Day view, the meal-detail popover, the
 * add-meal drawer, the read-only Nutrition view and "copy the meals from another template".
 * Runs against the real harness, no mocked APIs; the shell and week grid are covered by
 * plan-template-editor.spec.ts.
 */
import { request as apiRequest, type Locator, type Page } from '@playwright/test';
import { nutritionistTest as test, expect } from '../fixtures/auth';

const NUTRITIONIST_EMAIL = 'qa.nutri@formup.test';
const DAILY_KCAL = 2000;
const BREAKFAST_AND_LUNCH = ['Breakfast', 'Lunch'];

interface LoginResponseBody {
  accessToken: string;
}

interface CreatedBody {
  templateId?: string;
  recipeId?: string;
}

interface FoodsSearchBody {
  foods?: { foodId?: string }[];
}

type WeeksBody = {
  weekNumber: number;
  days: { dayOfWeek: number; meals: { kind: string; order: number; foods: unknown[]; recipes: unknown[] }[] }[];
}[];

async function withApi<T>(baseURL: string, run: (api: Awaited<ReturnType<typeof apiRequest.newContext>>, auth: Record<string, string>) => Promise<T>): Promise<T> {
  const password = process.env['QA_SEED_PASSWORD'];
  if (!password) {
    throw new Error('[plan-template-editor-views] QA_SEED_PASSWORD is not set. Copy .env.test.example to .env.test and fill it in.');
  }
  const api = await apiRequest.newContext({ baseURL });
  try {
    const login = await api.post('/auth/login', { data: { email: NUTRITIONIST_EMAIL, password } });
    if (!login.ok()) {
      throw new Error(`[plan-template-editor-views] login as qa.nutri returned ${login.status()}.`);
    }
    const { accessToken } = (await login.json()) as LoginResponseBody;
    return await run(api, { Authorization: `Bearer ${accessToken}` });
  } finally {
    await api.dispose();
  }
}

/** Seven days per week; each day holds one empty meal per kind. */
function buildWeeks(weekCount: number, kinds: string[]): WeeksBody {
  return Array.from({ length: weekCount }, (_, weekIndex) => ({
    weekNumber: weekIndex + 1,
    days: Array.from({ length: 7 }, (_, dayIndex) => ({
      dayOfWeek: dayIndex + 1,
      meals: kinds.map((kind, mealIndex) => ({ kind, order: mealIndex + 1, foods: [], recipes: [] })),
    })),
  }));
}

async function createTemplate(baseURL: string, name: string, weeks: WeeksBody): Promise<string> {
  return withApi(baseURL, async (api, headers) => {
    const response = await api.post('/nutrition/plan-templates', {
      headers,
      data: { name, goal: 'Maintain', globalSettings: { dailyKcal: DAILY_KCAL }, weeks },
    });
    if (!response.ok()) {
      throw new Error(`[plan-template-editor-views] POST /nutrition/plan-templates returned ${response.status()}.`);
    }
    const templateId = ((await response.json()) as CreatedBody).templateId;
    if (!templateId) {
      throw new Error('[plan-template-editor-views] create response carried no templateId.');
    }
    return templateId;
  });
}

async function deleteTemplate(baseURL: string, templateId: string): Promise<void> {
  await withApi(baseURL, (api, headers) => api.delete(`/nutrition/plan-templates/${templateId}`, { headers }));
}

async function createRecipe(baseURL: string, name: string): Promise<string> {
  return withApi(baseURL, async (api, headers) => {
    const search = await api.get('/foods/search?q=Apple', { headers });
    const foodId = ((await search.json()) as FoodsSearchBody).foods?.find((food) => food.foodId)?.foodId;
    if (!foodId) {
      throw new Error('[plan-template-editor-views] GET /foods/search?q=Apple returned no food.');
    }
    const response = await api.post('/recipes', {
      headers,
      data: {
        name,
        servings: 2,
        mealTypes: ['Lunch'],
        dietaryPreferences: [],
        foods: [{ foodExternalId: foodId, amountGrams: 200 }],
        steps: [{ description: 'Mix.' }],
      },
    });
    if (!response.ok()) {
      throw new Error(`[plan-template-editor-views] POST /recipes returned ${response.status()}.`);
    }
    const recipeId = ((await response.json()) as CreatedBody).recipeId;
    if (!recipeId) {
      throw new Error('[plan-template-editor-views] POST /recipes returned no recipeId.');
    }
    return recipeId;
  });
}

async function deleteRecipe(baseURL: string, recipeId: string): Promise<void> {
  await withApi(baseURL, (api, headers) => api.delete(`/recipes/${recipeId}`, { headers }));
}

function cell(page: Page, dayOfWeek: number, rowIndex: number): Locator {
  return page.locator(`[data-testid="meal-cell"][data-day="${dayOfWeek}"][data-row="${rowIndex}"]`);
}

/** Selects Monday's breakfast and adds the recipe to it with the library's + button. */
async function addRecipeToMondayBreakfast(page: Page, recipeName: string): Promise<void> {
  await cell(page, 1, 0).click();
  await page.getByLabel('Search recipes…').fill(recipeName);
  await page.getByRole('button', { name: new RegExp(`^Add ${recipeName} to the selected meal$`) }).click();
  await expect(cell(page, 1, 0)).toContainText(recipeName);
}

async function waitForSave(page: Page): Promise<void> {
  const put = page.waitForResponse(
    (response) => response.url().includes('/nutrition/plan-templates/') && response.request().method() === 'PUT',
  );
  await page.getByRole('button', { name: 'Save' }).click();
  expect((await put).ok()).toBe(true);
  await expect(page.getByTestId('save-status')).toHaveText('Saved');
}

test.describe('plan template editor views', () => {
  test('Day view: edit an amount, add and remove a meal, Save and reload', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const stamp = Date.now();
    const recipeName = `QA Views Recipe ${stamp}`;
    const recipeId = await createRecipe(origin, recipeName);
    const templateId = await createTemplate(origin, `QA Views Day ${stamp}`, buildWeeks(1, BREAKFAST_AND_LUNCH));
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await addRecipeToMondayBreakfast(page, recipeName);

      await page.getByRole('button', { name: 'Day', exact: true }).click();
      await expect(page.getByTestId('day-view')).toBeVisible();
      await expect(page.getByRole('tab', { name: /^Mon/ })).toHaveAttribute('aria-selected', 'true');
      await expect(page.getByTestId('meal-block')).toHaveCount(2);
      await expect(page.getByTestId('meal-share').first()).toBeVisible();

      // An invalid amount is flagged and never committed; a valid one is.
      const amount = page.getByLabel(`Amount of ${recipeName}`);
      await amount.fill('0');
      await expect(amount).toHaveAttribute('aria-invalid', 'true');
      await expect(page.getByTestId('save-status')).toHaveText('Unsaved changes');
      await amount.fill('2');
      await expect(amount).not.toHaveAttribute('aria-invalid', 'true');

      // Add a Dinner (it goes last) through the macro bar's button and the drawer, then remove it again.
      await page.getByTestId('day-add-meal').click();
      const drawer = page.getByRole('dialog');
      await drawer.getByRole('radio', { name: 'Dinner' }).click();
      await drawer.getByLabel('Note for the client').fill('Eat early.');
      await drawer.getByRole('button', { name: 'Add meal' }).click();
      await expect(page.getByTestId('meal-block')).toHaveCount(3);
      await expect(page.getByTestId('meal-block').nth(2).getByRole('heading', { name: 'Dinner' })).toBeVisible();
      await page.getByRole('button', { name: 'Remove Dinner' }).click();
      await expect(page.getByTestId('meal-block')).toHaveCount(2);

      await waitForSave(page);
      await page.reload();
      await page.getByRole('button', { name: 'Day', exact: true }).click();
      await expect(page.getByLabel(`Amount of ${recipeName}`)).toHaveValue('2');
    } finally {
      await deleteTemplate(origin, templateId);
      await deleteRecipe(origin, recipeId);
    }
  });

  test('Day view: Alt-dragging a meal card onto a filled meal asks Replace or Add, and one Undo restores it', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const stamp = Date.now();
    const breakfastName = `QA Views Day Breakfast ${stamp}`;
    const lunchName = `QA Views Day Lunch ${stamp}`;
    const breakfastId = await createRecipe(origin, breakfastName);
    const lunchId = await createRecipe(origin, lunchName);
    const templateId = await createTemplate(origin, `QA Views Day Copy ${stamp}`, buildWeeks(1, BREAKFAST_AND_LUNCH));
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await addRecipeToMondayBreakfast(page, breakfastName);
      await cell(page, 1, 1).click();
      await page.getByLabel('Search recipes…').fill(lunchName);
      await page.getByRole('button', { name: new RegExp(`^Add ${lunchName} to the selected meal$`) }).click();
      await waitForSave(page);

      await page.getByRole('button', { name: 'Day', exact: true }).click();
      const blocks = page.getByTestId('meal-block');
      await expect(blocks).toHaveCount(2);
      const from = await blocks.nth(0).locator('h3').boundingBox();
      const to = await blocks.nth(1).locator('h3').boundingBox();
      if (!from || !to) {
        throw new Error('[plan-template-editor-views] meal cards are not visible.');
      }
      // A plain drag within the day moves the meal; Alt (Option) held through the drag copies it instead.
      await page.keyboard.down('Alt');
      await page.mouse.move(from.x + 10, from.y + 5);
      await page.mouse.down();
      await page.mouse.move(from.x + 14, from.y + 20, { steps: 4 });
      await page.mouse.move(to.x + 10, to.y + 5, { steps: 15 });
      await expect(blocks.nth(1)).toHaveAttribute('data-drop-target', 'copy');
      await page.mouse.up();
      await page.keyboard.up('Alt');

      const dialog = page.getByTestId('card-drop-dialog');
      await expect(dialog).toBeVisible();
      await dialog.getByRole('button', { name: 'Replace', exact: true }).click();
      await expect(blocks.nth(1)).toContainText(breakfastName);
      await expect(blocks.nth(1)).not.toContainText(lunchName);
      await expect(page.getByTestId('save-status')).toHaveText('Unsaved changes');

      await page.getByRole('button', { name: 'Undo' }).click();
      await expect(blocks.nth(1)).toContainText(lunchName);
      await expect(blocks.nth(1)).not.toContainText(breakfastName);
      await expect(page.getByTestId('save-status')).toHaveText('Saved');
    } finally {
      await deleteTemplate(origin, templateId);
      await deleteRecipe(origin, breakfastId);
      await deleteRecipe(origin, lunchId);
    }
  });

  test('Day view: the Add meal drawer disables kinds the day has, and a new meal lands in kind order', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const templateId = await createTemplate(origin, `QA Views Kinds ${Date.now()}`, buildWeeks(1, ['Breakfast', 'Dinner']));
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await page.getByRole('button', { name: 'Day', exact: true }).click();
      await page.getByTestId('day-add-meal').click();
      const drawer = page.getByRole('dialog');
      await expect(drawer.getByRole('radio', { name: /^Breakfast/ })).toBeDisabled();
      await expect(drawer.getByRole('radio', { name: /^Dinner/ })).toBeDisabled();
      await expect(drawer.getByText('Already in this day')).toHaveCount(2);

      await drawer.getByRole('radio', { name: 'Lunch' }).click();
      await drawer.getByRole('button', { name: 'Add meal' }).click();
      await expect(page.locator('[data-testid="meal-block"] h3')).toHaveText(['Breakfast', 'Lunch', 'Dinner']);
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('meal detail popover: edit servings, copy to another weekday, close with Escape', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const stamp = Date.now();
    const recipeName = `QA Views Popover ${stamp}`;
    const recipeId = await createRecipe(origin, recipeName);
    const templateId = await createTemplate(origin, `QA Views Popover ${stamp}`, buildWeeks(1, BREAKFAST_AND_LUNCH));
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await addRecipeToMondayBreakfast(page, recipeName);

      await cell(page, 1, 0).click();
      const detail = page.getByTestId('meal-detail');
      await expect(detail).toBeVisible();

      // The library stays usable while the popover is open.
      await expect(detail.getByRole('heading')).toContainText('Breakfast');
      await detail.getByLabel(`Amount of ${recipeName}`).fill('3');

      await detail.getByRole('button', { name: 'Tue', exact: true }).click();
      await detail.getByRole('button', { name: 'Copy', exact: true }).click();
      await expect(cell(page, 2, 0)).toContainText(recipeName);

      await page.keyboard.press('Escape');
      await expect(detail).toHaveCount(0);
    } finally {
      await deleteTemplate(origin, templateId);
      await deleteRecipe(origin, recipeId);
    }
  });

  test('Nutrition view is read-only and colours meals against their share of the day', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const stamp = Date.now();
    const recipeName = `QA Views Nutrition ${stamp}`;
    const recipeId = await createRecipe(origin, recipeName);
    const templateId = await createTemplate(origin, `QA Views Nutrition ${stamp}`, buildWeeks(1, BREAKFAST_AND_LUNCH));
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await addRecipeToMondayBreakfast(page, recipeName);

      await page.getByRole('button', { name: 'Nutrition', exact: true }).click();
      await expect(page.getByTestId('nutrition-cell')).toHaveCount(14);
      await expect(page.getByTestId('meal-cell')).toHaveCount(0);
      await expect(page.getByTestId('nutrition-cell').first()).not.toHaveAttribute('data-status', 'empty');
      await expect(page.getByTestId('nutrition-cell').nth(1)).toHaveAttribute('data-status', 'empty');
      await expect(page.getByText(/Meal shares: breakfast 25 %/)).toBeVisible();

      await page.getByRole('button', { name: 'Meals', exact: true }).click();
      await expect(page.getByTestId('meal-cell')).toHaveCount(14);
    } finally {
      await deleteTemplate(origin, templateId);
      await deleteRecipe(origin, recipeId);
    }
  });

  test('the day total opens a popover with planned versus target for every nutrient, in both views', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const templateId = await createTemplate(origin, `QA Views Target ${Date.now()}`, buildWeeks(1, BREAKFAST_AND_LUNCH));
    try {
      await page.goto(`/plan-templates/${templateId}`);
      const dayTotal = page.getByTestId('day-total').first();
      await expect(dayTotal).toBeVisible();
      const before = await dayTotal.boundingBox();

      // Hovering never opens it; only a click does.
      const popover = page.getByTestId('day-target-popover');
      await dayTotal.hover();
      await page.waitForTimeout(400);
      await expect(popover).toHaveCount(0);
      await expect(dayTotal).toHaveAttribute('aria-expanded', 'false');

      await dayTotal.click();
      await expect(popover).toBeVisible();
      await expect(dayTotal).toHaveAttribute('aria-expanded', 'true');
      await expect(popover.getByTestId('day-target-row')).toHaveCount(5);
      await expect(popover.locator('[data-nutrient="kcal"]')).toContainText(/2\D?000/);
      // A nutrient the plan has no target for says so; it is never shown as 0 or NaN.
      await expect(popover).not.toContainText(/NaN|Infinity/);
      await expect(popover.getByTestId('day-target-legend')).toContainText('10 %');
      await expect(popover.getByTestId('day-target-legend')).toContainText('20 %');

      // Opening it leaves the tile the same size.
      const after = await dayTotal.boundingBox();
      expect(after?.height).toBeCloseTo(before?.height ?? 0, 0);

      await page.keyboard.press('Escape');
      await expect(popover).toHaveCount(0);

      await page.getByRole('button', { name: 'Nutrition', exact: true }).click();
      await page.getByTestId('day-total').first().click();
      await expect(page.getByTestId('day-target-popover').getByTestId('day-target-row')).toHaveCount(5);
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('Day view: hovering the nutrition bar shows its segment values', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const stamp = Date.now();
    const recipeName = `QA Views Bar ${stamp}`;
    const recipeId = await createRecipe(origin, recipeName);
    const templateId = await createTemplate(origin, `QA Views Bar ${stamp}`, buildWeeks(1, BREAKFAST_AND_LUNCH));
    try {
      // The bar only shows from the xl breakpoint (1280) up.
      await page.setViewportSize({ width: 1440, height: 900 });
      await page.goto(`/plan-templates/${templateId}`);
      await addRecipeToMondayBreakfast(page, recipeName);
      await page.getByRole('button', { name: 'Day', exact: true }).click();

      const bar = page.getByTestId('macro-bar').first();
      const values = page.getByTestId('macro-bar-values').first();
      await expect(bar).toBeVisible();
      await expect(values).toBeHidden();

      await bar.hover();
      await expect(values).toBeVisible();
      await expect(values).toContainText(/P\s*\d+/);
      await expect(values).toContainText(/C\s*\d+/);
      await expect(values).toContainText(/F\s*\d+/);

      // The meal header carries the meal's summed protein, carbs, fat and fiber.
      const mealMacros = page.getByTestId('meal-block').first().getByTestId('meal-macros');
      await expect(mealMacros).toBeVisible();
      await expect(mealMacros).toContainText(/P\s*\d+\s*g/);
      await expect(mealMacros).toContainText(/C\s*\d+\s*g/);
      await expect(mealMacros).toContainText(/F\s*\d+\s*g/);
      await expect(mealMacros).toContainText(/Fib\s*\d+\s*g/);
    } finally {
      await deleteTemplate(origin, templateId);
      await deleteRecipe(origin, recipeId);
    }
  });

  test('a week with one meal row fills the editor on a large screen, and both views keep the same tile height', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const templateId = await createTemplate(origin, `QA Views Fill ${Date.now()}`, buildWeeks(1, ['Breakfast']));
    try {
      await page.setViewportSize({ width: 1920, height: 1080 });
      await page.goto(`/plan-templates/${templateId}`);
      const scroll = page.getByTestId('editor-scroll');
      const mealCell = page.getByTestId('meal-cell').first();
      await expect(mealCell).toBeVisible();

      // Below the last row sit only the (hidden) legend, the grid padding and the area's bottom padding.
      const scrollBox = await scroll.boundingBox();
      const cellBox = await mealCell.boundingBox();
      if (!scrollBox || !cellBox) {
        throw new Error('[plan-template-editor-views] the editor area or a meal tile is not visible.');
      }
      const gapBelowRow = scrollBox.y + scrollBox.height - (cellBox.y + cellBox.height);
      expect(gapBelowRow).toBeLessThan(140);
      // One row stretches well beyond its 10rem minimum.
      expect(cellBox.height).toBeGreaterThan(200);

      await page.getByRole('button', { name: 'Nutrition', exact: true }).click();
      const nutritionBox = await page.getByTestId('nutrition-cell').first().boundingBox();
      expect(nutritionBox?.height).toBeCloseTo(cellBox.height, 0);
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('an empty week can copy the meals from another template', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const stamp = Date.now();
    const sourceName = `QA Views Source ${stamp}`;
    const sourceId = await createTemplate(origin, sourceName, buildWeeks(1, ['Breakfast', 'Lunch', 'Dinner']));
    const targetId = await createTemplate(origin, `QA Views Target ${stamp}`, buildWeeks(2, []));
    try {
      await page.goto(`/plan-templates/${targetId}`);
      await expect(page.getByTestId('meal-cell')).toHaveCount(0);

      await page.getByRole('button', { name: 'copy the meals from another template' }).click();
      const dialog = page.getByRole('dialog');
      await dialog.getByLabel('Search templates…').fill(sourceName);
      await dialog.getByRole('button', { name: new RegExp(sourceName) }).click();

      await expect(page.getByTestId('meal-cell')).toHaveCount(21);
      await expect(page.getByTestId('save-status')).toHaveText('Unsaved changes');

      // The second, still-empty week gets the same rows.
      await page.getByRole('tab', { name: 'Week 2' }).click();
      await expect(page.getByTestId('meal-cell')).toHaveCount(21);

      await waitForSave(page);
    } finally {
      await deleteTemplate(origin, targetId);
      await deleteTemplate(origin, sourceId);
    }
  });
});
