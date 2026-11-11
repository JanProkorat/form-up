/**
 * Plan template editor (epic #1237, task #1240): the shared plan editor shell and week grid,
 * hosted by the template editor page. Runs against the real harness, no mocked APIs.
 *
 * Covers the empty-state meal picker, drag a recipe onto a cell, select a cell and press + for an
 * ingredient, the day total, Save + reload, Undo, the unsaved-changes guard on the breadcrumb, the
 * 409 conflict (edits kept, nothing overwritten) and the trainer-only state.
 *
 * Lives under nutritionist/ because every plan-template endpoint is Nutritionist-only; the
 * trainer-only test imports `trainerTest` directly (same pattern as plan-templates.spec.ts).
 */
import { request as apiRequest, type Locator, type Page } from '@playwright/test';
import { nutritionistTest as test, trainerTest, expect } from '../fixtures/auth';

const NUTRITIONIST_EMAIL = 'qa.nutri@formup.test';
const DAILY_KCAL = 2000;

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

interface TemplateDetailBody {
  version?: number;
}

type WeeksBody = {
  weekNumber: number;
  days: { dayOfWeek: number; meals: { kind: string; order: number; foods: unknown[]; recipes: unknown[] }[] }[];
}[];

async function login(baseURL: string): Promise<string> {
  const password = process.env['QA_SEED_PASSWORD'];
  if (!password) {
    throw new Error('[plan-template-editor] QA_SEED_PASSWORD is not set. Copy .env.test.example to .env.test and fill it in.');
  }
  const api = await apiRequest.newContext({ baseURL });
  try {
    const response = await api.post('/auth/login', { data: { email: NUTRITIONIST_EMAIL, password } });
    if (!response.ok()) {
      throw new Error(`[plan-template-editor] login as qa.nutri returned ${response.status()}.`);
    }
    return ((await response.json()) as LoginResponseBody).accessToken;
  } finally {
    await api.dispose();
  }
}

/** Seven days per week; each day holds one empty meal per kind (none for an empty-state week). */
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
  const accessToken = await login(baseURL);
  const api = await apiRequest.newContext({ baseURL });
  try {
    const response = await api.post('/nutrition/plan-templates', {
      headers: { Authorization: `Bearer ${accessToken}` },
      data: { name, goal: 'Maintain', globalSettings: { dailyKcal: DAILY_KCAL }, weeks },
    });
    if (!response.ok()) {
      throw new Error(`[plan-template-editor] POST /nutrition/plan-templates returned ${response.status()}.`);
    }
    const body = (await response.json()) as CreatedBody;
    if (!body.templateId) {
      throw new Error('[plan-template-editor] create response carried no templateId.');
    }
    return body.templateId;
  } finally {
    await api.dispose();
  }
}

async function deleteTemplate(baseURL: string, templateId: string): Promise<void> {
  const accessToken = await login(baseURL);
  const api = await apiRequest.newContext({ baseURL });
  try {
    await api.delete(`/nutrition/plan-templates/${templateId}`, { headers: { Authorization: `Bearer ${accessToken}` } });
  } finally {
    await api.dispose();
  }
}

async function createRecipe(baseURL: string, name: string): Promise<string> {
  const accessToken = await login(baseURL);
  const api = await apiRequest.newContext({ baseURL });
  try {
    const search = await api.get('/foods/search?q=Apple', { headers: { Authorization: `Bearer ${accessToken}` } });
    const foodId = ((await search.json()) as FoodsSearchBody).foods?.find((food) => food.foodId)?.foodId;
    if (!foodId) {
      throw new Error('[plan-template-editor] GET /foods/search?q=Apple returned no food.');
    }
    const response = await api.post('/recipes', {
      headers: { Authorization: `Bearer ${accessToken}` },
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
      throw new Error(`[plan-template-editor] POST /recipes returned ${response.status()}.`);
    }
    const recipeId = ((await response.json()) as CreatedBody).recipeId;
    if (!recipeId) {
      throw new Error('[plan-template-editor] POST /recipes returned no recipeId.');
    }
    return recipeId;
  } finally {
    await api.dispose();
  }
}

async function deleteRecipe(baseURL: string, recipeId: string): Promise<void> {
  const accessToken = await login(baseURL);
  const api = await apiRequest.newContext({ baseURL });
  try {
    await api.delete(`/recipes/${recipeId}`, { headers: { Authorization: `Bearer ${accessToken}` } });
  } finally {
    await api.dispose();
  }
}

/** Reads the template's current version, then replaces it from outside the editor (bumps the version). */
async function changeTemplateElsewhere(baseURL: string, templateId: string, name: string, weeks: WeeksBody): Promise<void> {
  const accessToken = await login(baseURL);
  const api = await apiRequest.newContext({ baseURL });
  try {
    const headers = { Authorization: `Bearer ${accessToken}` };
    const detail = await api.get(`/nutrition/plan-templates/${templateId}`, { headers });
    const { version } = (await detail.json()) as TemplateDetailBody;
    const response = await api.put(`/nutrition/plan-templates/${templateId}`, {
      headers,
      data: { name, goal: 'Maintain', globalSettings: { dailyKcal: DAILY_KCAL }, weeks, supplements: [], version },
    });
    if (!response.ok()) {
      throw new Error(`[plan-template-editor] PUT /nutrition/plan-templates/${templateId} returned ${response.status()}.`);
    }
  } finally {
    await api.dispose();
  }
}

function cell(page: Page, dayOfWeek: number, rowIndex: number): Locator {
  return page.locator(`[data-testid="meal-cell"][data-day="${dayOfWeek}"][data-row="${rowIndex}"]`);
}

/** Drags with real pointer moves: dnd-kit only starts a drag after the pointer has travelled. */
async function dragTo(page: Page, handle: Locator, target: Locator): Promise<void> {
  const from = await handle.boundingBox();
  const to = await target.boundingBox();
  if (!from || !to) {
    throw new Error('[plan-template-editor] drag source or target is not visible.');
  }
  await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
  await page.mouse.down();
  await page.mouse.move(from.x + from.width / 2 + 12, from.y + from.height / 2 + 12, { steps: 4 });
  await page.mouse.move(to.x + to.width / 2, to.y + to.height / 2, { steps: 12 });
  await page.mouse.up();
}

const LUNCH_AND_BREAKFAST = ['Breakfast', 'Lunch'];

test.describe('plan template editor', () => {
  test('the empty-state picker gives every empty week the common meal rows, and Save keeps them', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const name = `QA Editor Empty ${Date.now()}`;
    const templateId = await createTemplate(origin, name, buildWeeks(2, []));
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await expect(page.getByRole('heading', { name })).toBeAttached();
      await expect(page.getByText('Which meals does a day have?')).toBeVisible();
      await expect(page.getByTestId('meal-cell')).toHaveCount(0);

      await page.getByRole('button', { name: /Breakfast · Morning snack · Lunch · Dinner/ }).click();
      await expect(page.getByTestId('meal-cell')).toHaveCount(28);
      await expect(page.getByText('Which meals does a day have?')).toHaveCount(0);
      await expect(page.getByTestId('save-status')).toHaveText('Unsaved changes');

      await page.getByRole('tab', { name: 'Week 2' }).click();
      await expect(page.getByTestId('meal-cell')).toHaveCount(28);

      const put = page.waitForResponse(
        (response) => response.url().includes('/nutrition/plan-templates/') && response.request().method() === 'PUT',
      );
      await page.getByRole('button', { name: 'Save' }).click();
      expect((await put).ok()).toBe(true);
      await expect(page.getByTestId('save-status')).toHaveText('Saved');

      await page.reload();
      await expect(page.getByTestId('meal-cell')).toHaveCount(28);
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('drag a recipe onto a cell, add an ingredient with +, see the day total, Save, reload and Undo', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const stamp = Date.now();
    const name = `QA Editor Fill ${stamp}`;
    const recipeName = `QA Editor Recipe ${stamp}`;
    const recipeId = await createRecipe(origin, recipeName);
    const templateId = await createTemplate(origin, name, buildWeeks(1, LUNCH_AND_BREAKFAST));
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await expect(page.getByTestId('meal-cell')).toHaveCount(14);
      await expect(page.getByTestId('day-total').first()).toContainText('0 kcal');

      // Drag the recipe onto Monday's first meal.
      await page.getByLabel('Search recipes…').fill(recipeName);
      const recipeCard = page.getByTestId('library-card').filter({ hasText: recipeName });
      await expect(recipeCard).toHaveCount(1);
      await dragTo(page, recipeCard, cell(page, 1, 0));
      await expect(cell(page, 1, 0)).toContainText(recipeName);
      await expect(page.getByTestId('day-total').first()).not.toContainText(/^0 kcal/);
      await expect(page.getByTestId('save-status')).toHaveText('Unsaved changes');

      // Monday's day card is one lean row: total against the kcal target, then the macro dots.
      await page.getByRole('button', { name: 'Day', exact: true }).click();
      await expect(page.getByTestId('day-summary')).toContainText('Day total');
      await expect(page.getByTestId('day-summary')).toContainText(/ \/ [\d,.\s]+ kcal/);
      await expect(page.getByTestId('day-summary')).toContainText(/P \d+/);
      await page.getByRole('button', { name: 'Week', exact: true }).click();

      // Select Tuesday's lunch and press + on an ingredient.
      await cell(page, 2, 1).click();
      await page.getByRole('tab', { name: 'Ingredients' }).click();
      await page.getByLabel('Search ingredients…').fill('Apple');
      const appleCards = page.getByTestId('library-card');
      await expect(appleCards.first()).toContainText('Apple');
      await expect(appleCards.filter({ hasNotText: 'Apple' })).toHaveCount(0);
      const addButton = appleCards.first().getByRole('button', { name: /^Add .* to the selected meal$/ });
      await expect(addButton).toBeEnabled();
      await addButton.click();
      await expect(cell(page, 2, 1)).not.toContainText('Empty');

      const put = page.waitForResponse(
        (response) => response.url().includes('/nutrition/plan-templates/') && response.request().method() === 'PUT',
      );
      await page.getByRole('button', { name: 'Save' }).click();
      expect((await put).ok()).toBe(true);
      await expect(page.getByTestId('save-status')).toHaveText('Saved');

      await page.reload();
      await expect(cell(page, 1, 0)).toContainText(recipeName);
      await expect(cell(page, 2, 1)).not.toContainText('Empty');

      // Undo reverts the last edit.
      await page.getByLabel('Search recipes…').fill(recipeName);
      await cell(page, 3, 0).click();
      await page.getByRole('tab', { name: 'Recipes' }).click();
      await page.getByRole('button', { name: new RegExp(`^Add ${recipeName} to the selected meal$`) }).click();
      await expect(cell(page, 3, 0)).toContainText(recipeName);
      await page.getByRole('button', { name: 'Undo' }).click();
      await expect(cell(page, 3, 0)).toContainText('Empty');
      await expect(page.getByTestId('save-status')).toHaveText('Saved');
    } finally {
      await deleteTemplate(origin, templateId);
      await deleteRecipe(origin, recipeId);
    }
  });

  test('the breadcrumb asks before discarding unsaved edits', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const name = `QA Editor Leave ${Date.now()}`;
    const templateId = await createTemplate(origin, name, buildWeeks(1, LUNCH_AND_BREAKFAST));
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await page.getByLabel('Plan name').fill(`${name} edited`);
      await page.getByRole('button', { name: 'Plan templates', exact: true }).click();

      const dialog = page.getByRole('dialog');
      await expect(dialog.getByRole('heading', { name: 'Leave without saving?' })).toBeVisible();
      await dialog.getByRole('button', { name: 'Keep editing' }).click();
      await expect(page).toHaveURL(new RegExp(`/plan-templates/${templateId}$`));

      await page.getByRole('button', { name: 'Plan templates', exact: true }).click();
      await page.getByRole('dialog').getByRole('button', { name: 'Leave' }).click();
      await expect(page).toHaveURL(/\/plan-templates$/);
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('Redo replays an undone edit, and a new edit after Undo clears Redo', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const name = `QA Editor Redo ${Date.now()}`;
    const templateId = await createTemplate(origin, name, buildWeeks(1, LUNCH_AND_BREAKFAST));
    try {
      await page.goto(`/plan-templates/${templateId}`);
      const undo = page.getByRole('button', { name: 'Undo' });
      const redo = page.getByRole('button', { name: 'Redo' });
      const nameInput = page.getByLabel('Plan name');
      await expect(undo).toBeDisabled();
      await expect(redo).toBeDisabled();

      await nameInput.fill(`${name} A`);
      await expect(undo).toBeEnabled();
      await expect(redo).toBeDisabled();

      await undo.click();
      await expect(nameInput).toHaveValue(name);
      await expect(redo).toBeEnabled();
      await expect(undo).toBeDisabled();

      await redo.click();
      await expect(nameInput).toHaveValue(`${name} A`);
      await expect(redo).toBeDisabled();

      // A new edit after Undo drops the redo history.
      await undo.click();
      await expect(redo).toBeEnabled();
      await nameInput.fill(`${name} B`);
      await expect(redo).toBeDisabled();

      // Keyboard: Ctrl+Z undoes outside a text field, Ctrl+Shift+Z redoes.
      await nameInput.blur();
      await page.keyboard.press('Control+z');
      await expect(nameInput).toHaveValue(name);
      await page.keyboard.press('Control+Shift+z');
      await expect(nameInput).toHaveValue(`${name} B`);
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('the sidebar choice is one stored setting for every page, the editor included', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const templateId = await createTemplate(origin, `QA Editor Sidebar ${Date.now()}`, buildWeeks(1, LUNCH_AND_BREAKFAST));
    try {
      await page.goto('/plan-templates');
      await page.waitForLoadState('networkidle');
      // The editor page also has the library <aside>; pick the app sidebar by its main navigation.
      const aside = page.locator('aside').filter({ has: page.getByRole('navigation', { name: 'Navigation', exact: true }) });
      const navigation = page.getByRole('navigation', { name: 'Navigation', exact: true });
      await expect(aside).toHaveCSS('width', '248px');

      await navigation.getByRole('button', { name: 'Collapse navigation' }).click();
      await expect(aside).toHaveCSS('width', '64px');

      // The editor opens with the stored choice, collapsed here.
      await page.goto(`/plan-templates/${templateId}`);
      await page.waitForLoadState('networkidle');
      await expect(aside).toHaveCSS('width', '64px');

      // Toggling in the editor writes the same choice, so Back and a reload keep it.
      await navigation.getByRole('button', { name: 'Expand navigation' }).click();
      await expect(aside).toHaveCSS('width', '248px');
      expect(await page.evaluate(() => window.localStorage.getItem('sidebar-collapsed'))).toBe('0');

      await page.getByRole('button', { name: 'Back to Plan templates' }).click();
      await expect(page).toHaveURL(/\/plan-templates$/);
      await expect(aside).toHaveCSS('width', '248px');

      await page.reload();
      await expect(aside).toHaveCSS('width', '248px');
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('Back asks before discarding unsaved edits, like the breadcrumb', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const name = `QA Editor Back ${Date.now()}`;
    const templateId = await createTemplate(origin, name, buildWeeks(1, LUNCH_AND_BREAKFAST));
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await page.getByLabel('Plan name').fill(`${name} edited`);
      await page.getByRole('button', { name: 'Back to Plan templates' }).click();

      const dialog = page.getByRole('dialog');
      await expect(dialog.getByRole('heading', { name: 'Leave without saving?' })).toBeVisible();
      await dialog.getByRole('button', { name: 'Keep editing' }).click();
      await expect(page).toHaveURL(new RegExp(`/plan-templates/${templateId}$`));

      await page.getByRole('button', { name: 'Back to Plan templates' }).click();
      await page.getByRole('dialog').getByRole('button', { name: 'Leave' }).click();
      await expect(page).toHaveURL(/\/plan-templates$/);
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('the sidebar asks before discarding unsaved edits, and Keep editing keeps them', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const name = `QA Editor Sidebar Leave ${Date.now()}`;
    const templateId = await createTemplate(origin, name, buildWeeks(1, LUNCH_AND_BREAKFAST));
    try {
      await page.goto(`/plan-templates/${templateId}`);
      const nameInput = page.getByLabel('Plan name');
      await nameInput.fill(`${name} edited`);
      const sidebarLink = page
        .getByRole('navigation', { name: 'Navigation', exact: true })
        .getByRole('link', { name: 'Plan templates' });
      await sidebarLink.click();

      const dialog = page.getByRole('dialog');
      await expect(dialog.getByRole('heading', { name: 'Leave without saving?' })).toBeVisible();
      await dialog.getByRole('button', { name: 'Keep editing' }).click();
      await expect(dialog).toHaveCount(0);
      await expect(page).toHaveURL(new RegExp(`/plan-templates/${templateId}$`));
      await expect(nameInput).toHaveValue(`${name} edited`);

      await sidebarLink.click();
      await page.getByRole('dialog').getByRole('button', { name: 'Leave' }).click();
      await expect(page).toHaveURL(/\/plan-templates$/);
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('browser Back asks before discarding unsaved edits, and Keep editing stays put', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const name = `QA Editor History Leave ${Date.now()}`;
    const templateId = await createTemplate(origin, name, buildWeeks(1, LUNCH_AND_BREAKFAST));
    try {
      // Open the editor from the list so history holds an in-app entry to go back to.
      await page.goto('/plan-templates');
      await page.getByRole('row').filter({ hasText: name }).getByText(name).first().click();
      await expect(page).toHaveURL(new RegExp(`/plan-templates/${templateId}$`));

      const nameInput = page.getByLabel('Plan name');
      await nameInput.fill(`${name} edited`);
      await page.goBack();

      const dialog = page.getByRole('dialog');
      await expect(dialog.getByRole('heading', { name: 'Leave without saving?' })).toBeVisible();
      await dialog.getByRole('button', { name: 'Keep editing' }).click();
      await expect(dialog).toHaveCount(0);
      await expect(page).toHaveURL(new RegExp(`/plan-templates/${templateId}$`));
      await expect(nameInput).toHaveValue(`${name} edited`);
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('the sidebar navigates without a dialog when nothing was edited', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const templateId = await createTemplate(origin, `QA Editor Clean Leave ${Date.now()}`, buildWeeks(1, LUNCH_AND_BREAKFAST));
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await expect(page.getByTestId('meal-cell')).toHaveCount(14);
      await page
        .getByRole('navigation', { name: 'Navigation', exact: true })
        .getByRole('link', { name: 'Plan templates' })
        .click();
      await expect(page).toHaveURL(/\/plan-templates$/);
      await expect(page.getByRole('dialog')).toHaveCount(0);
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('a save after the template changed elsewhere shows a conflict and keeps the edits', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const name = `QA Editor Conflict ${Date.now()}`;
    const weeks = buildWeeks(1, LUNCH_AND_BREAKFAST);
    const templateId = await createTemplate(origin, name, weeks);
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await expect(page.getByTestId('meal-cell')).toHaveCount(14);

      await changeTemplateElsewhere(origin, templateId, `${name} (other session)`, weeks);

      const edited = `${name} (mine)`;
      await page.getByLabel('Plan name').fill(edited);
      const put = page.waitForResponse(
        (response) => response.url().includes('/nutrition/plan-templates/') && response.request().method() === 'PUT',
      );
      await page.getByRole('button', { name: 'Save' }).click();
      expect((await put).status()).toBe(409);

      await expect(page.getByRole('alert')).toContainText('changed somewhere else');
      await expect(page.getByLabel('Plan name')).toHaveValue(edited);
      await expect(page.getByTestId('save-status')).toHaveText('Unsaved changes');
      await expect(page.getByRole('button', { name: 'Save' })).toBeEnabled();
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('a template that does not exist shows a not-found state with a way back', async ({ page }) => {
    await page.goto('/plan-templates/00000000-0000-4000-8000-000000000000');
    await expect(page.getByText('Template not found')).toBeVisible();
    await page.getByRole('main').getByRole('link', { name: 'Plan templates' }).click();
    await expect(page).toHaveURL(/\/plan-templates$/);
  });
});

trainerTest.describe('plan template editor for a trainer-only coach', () => {
  trainerTest('redirects a trainer-only coach to clients and sends no template request', async ({ page }) => {
    const templateRequests: string[] = [];
    page.on('request', (request) => {
      if (request.url().includes('/nutrition/plan-templates')) {
        templateRequests.push(request.url());
      }
    });
    await page.goto('/plan-templates/00000000-0000-4000-8000-000000000000');
    await expect(page).toHaveURL(/\/clients$/);
    expect(templateRequests).toEqual([]);
  });
});
