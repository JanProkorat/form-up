/**
 * Plan template editor, Template info tab (epic #1237, task #1268): editing the template's own
 * data (name, goal, daily targets) in the side panel, saving it with the editor's Save, and the
 * tab staying selected while the toolbar toggles change. Runs against the real harness, no mocked
 * APIs; the shell and grid are covered by plan-template-editor.spec.ts.
 */
import { request as apiRequest } from '@playwright/test';
import { nutritionistTest as test, expect } from '../fixtures/auth';

const NUTRITIONIST_EMAIL = 'qa.nutri@formup.test';
const DAILY_KCAL = 2000;

interface LoginResponseBody {
  accessToken: string;
}

interface CreatedBody {
  templateId?: string;
}

interface TemplateBody {
  name?: string;
  goal?: string | null;
  globalSettings?: { dailyKcal?: number | null; proteinGrams?: number | null } | null;
}

async function withApi<T>(
  baseURL: string,
  run: (api: Awaited<ReturnType<typeof apiRequest.newContext>>, auth: Record<string, string>) => Promise<T>,
): Promise<T> {
  const password = process.env['QA_SEED_PASSWORD'];
  if (!password) {
    throw new Error('[plan-template-info] QA_SEED_PASSWORD is not set. Copy .env.test.example to .env.test and fill it in.');
  }
  const api = await apiRequest.newContext({ baseURL });
  try {
    const login = await api.post('/auth/login', { data: { email: NUTRITIONIST_EMAIL, password } });
    if (!login.ok()) {
      throw new Error(`[plan-template-info] login as qa.nutri returned ${login.status()}.`);
    }
    const { accessToken } = (await login.json()) as LoginResponseBody;
    return await run(api, { Authorization: `Bearer ${accessToken}` });
  } finally {
    await api.dispose();
  }
}

/** `count` weeks of seven days, each holding an empty breakfast. */
function buildWeeks(count: number) {
  return Array.from({ length: count }, (_, weekIndex) => ({
    weekNumber: weekIndex + 1,
    days: Array.from({ length: 7 }, (_day, dayIndex) => ({
      dayOfWeek: dayIndex + 1,
      meals: [{ kind: 'Breakfast', order: 1, foods: [], recipes: [] }],
    })),
  }));
}

async function createTemplate(baseURL: string, name: string, weekCount = 1): Promise<string> {
  return withApi(baseURL, async (api, headers) => {
    const response = await api.post('/nutrition/plan-templates', {
      headers,
      data: { name, goal: 'Maintain', globalSettings: { dailyKcal: DAILY_KCAL }, weeks: buildWeeks(weekCount) },
    });
    if (!response.ok()) {
      throw new Error(`[plan-template-info] POST /nutrition/plan-templates returned ${response.status()}.`);
    }
    const templateId = ((await response.json()) as CreatedBody).templateId;
    if (!templateId) {
      throw new Error('[plan-template-info] create response carried no templateId.');
    }
    return templateId;
  });
}

interface RecipeCreatedBody {
  recipeId?: string;
}

interface FoodsSearchBody {
  foods?: { foodId?: string }[];
}

async function createRecipe(baseURL: string, name: string, mealType: string): Promise<string> {
  return withApi(baseURL, async (api, headers) => {
    const search = await api.get('/foods/search?q=Apple', { headers });
    const foodId = ((await search.json()) as FoodsSearchBody).foods?.find((food) => food.foodId)?.foodId;
    if (!foodId) {
      throw new Error('[plan-template-info] GET /foods/search?q=Apple returned no food.');
    }
    const response = await api.post('/recipes', {
      headers,
      data: {
        name,
        servings: 2,
        mealTypes: [mealType],
        dietaryPreferences: [],
        foods: [{ foodExternalId: foodId, amountGrams: 200 }],
        steps: [{ description: 'Mix.' }],
      },
    });
    if (!response.ok()) {
      throw new Error(`[plan-template-info] POST /recipes returned ${response.status()}.`);
    }
    const recipeId = ((await response.json()) as RecipeCreatedBody).recipeId;
    if (!recipeId) {
      throw new Error('[plan-template-info] POST /recipes returned no recipeId.');
    }
    return recipeId;
  });
}

async function deleteRecipe(baseURL: string, recipeId: string): Promise<void> {
  await withApi(baseURL, (api, headers) => api.delete(`/recipes/${recipeId}`, { headers }));
}

async function readTemplate(baseURL: string, templateId: string): Promise<TemplateBody> {
  return withApi(baseURL, async (api, headers) => {
    const response = await api.get(`/nutrition/plan-templates/${templateId}`, { headers });
    return (await response.json()) as TemplateBody;
  });
}

async function deleteTemplate(baseURL: string, templateId: string): Promise<void> {
  await withApi(baseURL, (api, headers) => api.delete(`/nutrition/plan-templates/${templateId}`, { headers }));
}

test.describe('Plan template editor: Template info tab', () => {
  test('edit the name and a target, Save, reload shows the new values', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const stamp = Date.now();
    const templateId = await createTemplate(origin, `QA Info ${stamp}`);
    const newName = `QA Info Renamed ${stamp}`;
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await page.getByRole('tab', { name: 'Template info' }).click();

      const name = page.getByLabel('Template name');
      await expect(name).toHaveValue(`QA Info ${stamp}`);
      await name.fill(newName);
      await page.getByLabel('Calories').fill('2400');
      await page.getByLabel('Protein', { exact: true }).fill('150');
      await expect(page.getByTestId('info-week-average')).toContainText(/\/ 2,?400 kcal/);
      await expect(page.getByTestId('info-week-average')).toContainText('Week 1 · avg per day');
      await expect(page.getByTestId('save-status')).toHaveText('Unsaved changes');

      // Goal is optional: the selected chip clears when pressed again.
      const maintain = page.getByRole('button', { name: 'Maintain', exact: true });
      await expect(maintain).toHaveAttribute('aria-pressed', 'true');
      await maintain.click();
      await expect(maintain).toHaveAttribute('aria-pressed', 'false');

      const put = page.waitForResponse(
        (response) => response.url().includes('/nutrition/plan-templates/') && response.request().method() === 'PUT',
      );
      await page.getByRole('button', { name: 'Save' }).click();
      expect((await put).ok()).toBe(true);
      await expect(page.getByTestId('save-status')).toHaveText('Saved');

      await page.reload();
      await page.getByRole('tab', { name: 'Template info' }).click();
      await expect(page.getByLabel('Template name')).toHaveValue(newName);
      await expect(page.getByLabel('Calories')).toHaveValue('2400');
      await expect(page.getByLabel('Protein', { exact: true })).toHaveValue('150');
      await expect(page.getByRole('button', { name: 'Maintain', exact: true })).toHaveAttribute(
        'aria-pressed',
        'false',
      );

      const stored = await readTemplate(origin, templateId);
      expect(stored.name).toBe(newName);
      expect(stored.goal ?? null).toBeNull();
      expect(stored.globalSettings?.dailyKcal).toBe(2400);
      expect(stored.globalSettings?.proteinGrams).toBe(150);
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('an empty name shows an inline error and blocks Save', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const templateId = await createTemplate(origin, `QA Info Name ${Date.now()}`);
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await page.getByRole('tab', { name: 'Template info' }).click();
      await expect(page.getByTestId('info-name-error')).toHaveCount(0);

      await page.getByLabel('Template name').fill('');
      await expect(page.getByTestId('info-name-error')).toBeVisible();
      await expect(page.getByRole('button', { name: 'Save' })).toBeDisabled();

      await page.getByLabel('Template name').fill('Back again');
      await expect(page.getByTestId('info-name-error')).toHaveCount(0);
      await expect(page.getByRole('button', { name: 'Save' })).toBeEnabled();
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('the chosen tab stays while switching Week/Day and Meals/Nutrition', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const templateId = await createTemplate(origin, `QA Info Tab ${Date.now()}`);
    try {
      await page.goto(`/plan-templates/${templateId}`);
      const infoTab = page.getByRole('tab', { name: 'Template info' });
      await infoTab.click();
      await expect(infoTab).toHaveAttribute('aria-selected', 'true');

      await page.getByRole('button', { name: 'Day', exact: true }).click();
      await expect(infoTab).toHaveAttribute('aria-selected', 'true');
      await expect(page.getByLabel('Template name')).toBeVisible();

      await page.getByRole('button', { name: 'Week', exact: true }).click();
      await page.getByRole('button', { name: 'Nutrition', exact: true }).click();
      await expect(infoTab).toHaveAttribute('aria-selected', 'true');
      await page.getByRole('button', { name: 'Meals', exact: true }).click();
      await expect(infoTab).toHaveAttribute('aria-selected', 'true');

      // Usage line: a fresh private template is used by nobody.
      await expect(page.getByTestId('info-usage')).toContainText('0 clients');
      await expect(page.getByTestId('info-usage')).toContainText('Private · only you');

      await page.getByRole('tab', { name: 'Library' }).click();
      await expect(page.getByLabel('Search recipes…')).toBeVisible();
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('the week average card follows the selected week', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const templateId = await createTemplate(origin, `QA Info Avg ${Date.now()}`, 2);
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await page.getByRole('tab', { name: 'Template info' }).click();
      const card = page.getByTestId('info-week-average');
      await expect(card).toContainText('Week 1 · avg per day');
      await expect(card).toContainText(`/ ${DAILY_KCAL.toLocaleString('en-US')} kcal`);

      await page.getByRole('tab', { name: 'Week 2' }).click();
      await expect(card).toContainText('Week 2 · avg per day');
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('the collapsed strip opens the panel on the tab that was clicked', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const templateId = await createTemplate(origin, `QA Info Strip ${Date.now()}`);
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await page.getByRole('button', { name: 'Hide library' }).click();
      const strip = page.getByTestId('side-panel-strip');
      await expect(strip.getByRole('button', { name: 'Show library' })).toBeVisible();

      await strip.getByTestId('side-strip-info').click();
      await expect(page.getByRole('tab', { name: 'Template info' })).toHaveAttribute('aria-selected', 'true');
      await expect(page.getByLabel('Template name')).toBeVisible();

      await page.getByRole('button', { name: 'Hide library' }).click();
      await strip.getByTestId('side-strip-library').click();
      await expect(page.getByRole('tab', { name: 'Library' })).toHaveAttribute('aria-selected', 'true');
      await expect(page.getByLabel('Search recipes…')).toBeVisible();
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('the library filter popover narrows the recipe list live and Clear all restores it', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const stamp = Date.now();
    const breakfastName = `QA Filter Breakfast ${stamp}`;
    const dinnerName = `QA Filter Dinner ${stamp}`;
    const breakfastId = await createRecipe(origin, breakfastName, 'Breakfast');
    const dinnerId = await createRecipe(origin, dinnerName, 'Dinner');
    const templateId = await createTemplate(origin, `QA Filter ${stamp}`);
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await page.getByLabel('Search recipes…').fill(`QA Filter`);
      const panel = page.getByRole('complementary', { name: 'Library' });
      const cards = panel.getByTestId('library-card').filter({ hasText: String(stamp) });
      await expect(cards).toHaveCount(2);

      await panel.getByRole('button', { name: 'Filters' }).click();
      const filters = page.getByTestId('library-filters');
      await filters.getByRole('button', { name: 'Breakfast', exact: true }).click();
      await expect(cards).toHaveCount(1);
      await expect(cards.first()).toContainText(breakfastName);
      await expect(panel.getByTestId('library-filter-count')).toHaveText('1');
      await expect(filters.getByTestId('library-filters-match')).toContainText('recipe');

      await filters.getByTestId('library-filters-clear').click();
      await expect(cards).toHaveCount(2);
      await expect(panel.getByTestId('library-filter-count')).toHaveCount(0);
    } finally {
      await deleteTemplate(origin, templateId);
      await deleteRecipe(origin, breakfastId);
      await deleteRecipe(origin, dinnerId);
    }
  });
});
