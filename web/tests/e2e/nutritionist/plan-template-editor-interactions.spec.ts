/**
 * Plan template editor interactions (epic #1237, task #1258): dragging a meal card to another cell
 * of the week grid, the library info popover, the library staying open in the Nutrition view, and
 * the placeholder tiles and dot-style macros. Runs against the real harness, no mocked APIs; the
 * shell, grid and views are covered by plan-template-editor.spec.ts and
 * plan-template-editor-views.spec.ts.
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

async function withApi<T>(
  baseURL: string,
  run: (api: Awaited<ReturnType<typeof apiRequest.newContext>>, auth: Record<string, string>) => Promise<T>,
): Promise<T> {
  const password = process.env['QA_SEED_PASSWORD'];
  if (!password) {
    throw new Error('[plan-template-editor-interactions] QA_SEED_PASSWORD is not set. Copy .env.test.example to .env.test and fill it in.');
  }
  const api = await apiRequest.newContext({ baseURL });
  try {
    const login = await api.post('/auth/login', { data: { email: NUTRITIONIST_EMAIL, password } });
    if (!login.ok()) {
      throw new Error(`[plan-template-editor-interactions] login as qa.nutri returned ${login.status()}.`);
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

async function createTemplate(baseURL: string, name: string): Promise<string> {
  return withApi(baseURL, async (api, headers) => {
    const response = await api.post('/nutrition/plan-templates', {
      headers,
      data: { name, goal: 'Maintain', globalSettings: { dailyKcal: DAILY_KCAL }, weeks: buildWeeks(1, BREAKFAST_AND_LUNCH) },
    });
    if (!response.ok()) {
      throw new Error(`[plan-template-editor-interactions] POST /nutrition/plan-templates returned ${response.status()}.`);
    }
    const templateId = ((await response.json()) as CreatedBody).templateId;
    if (!templateId) {
      throw new Error('[plan-template-editor-interactions] create response carried no templateId.');
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
      throw new Error('[plan-template-editor-interactions] GET /foods/search?q=Apple returned no food.');
    }
    const response = await api.post('/recipes', {
      headers,
      data: {
        name,
        servings: 2,
        mealTypes: ['Lunch'],
        dietaryPreferences: [],
        foods: [{ foodExternalId: foodId, amountGrams: 200 }],
        steps: [{ description: 'Whisk the secret sauce.' }],
      },
    });
    if (!response.ok()) {
      throw new Error(`[plan-template-editor-interactions] POST /recipes returned ${response.status()}.`);
    }
    const recipeId = ((await response.json()) as CreatedBody).recipeId;
    if (!recipeId) {
      throw new Error('[plan-template-editor-interactions] POST /recipes returned no recipeId.');
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

/** The recipes and ingredients panel; the editor page has a second <aside> (the app sidebar). */
function libraryPanel(page: Page): Locator {
  return page.getByRole('complementary', { name: 'Library' });
}

function libraryCard(page: Page, name: string): Locator {
  return libraryPanel(page).getByTestId('library-card').filter({ hasText: name });
}

/** Drags with real pointer moves: dnd-kit only starts a drag after the pointer has travelled. */
/** Scrolls the element into view, then waits until two reads of its box match (popover animations settled). */
async function settledBox(locator: Locator): Promise<{ x: number; y: number; width: number; height: number } | null> {
  await locator.scrollIntoViewIfNeeded();
  let previous = '';
  let box = await locator.boundingBox();
  for (let attempt = 0; attempt < 20; attempt += 1) {
    const current = JSON.stringify(box);
    if (current === previous) {
      return box;
    }
    previous = current;
    await locator.page().waitForTimeout(50);
    box = await locator.boundingBox();
  }
  return box;
}

async function dragTo(page: Page, handle: Locator, target: Locator): Promise<void> {
  // Scroll both ends into view first, so reading one box cannot move the other.
  await handle.scrollIntoViewIfNeeded();
  await target.scrollIntoViewIfNeeded();
  const from = await settledBox(handle);
  const to = await settledBox(target);
  if (!from || !to) {
    throw new Error('[plan-template-editor-interactions] drag source or target is not visible.');
  }
  await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
  await page.mouse.down();
  await page.mouse.move(from.x + from.width / 2 + 12, from.y + from.height / 2 + 12, { steps: 4 });
  await page.mouse.move(to.x + to.width / 2, to.y + to.height / 2, { steps: 12 });
  await page.mouse.up();
}

/** Selects the day's breakfast and adds the recipe to it with the library's + button. */
async function addRecipeToBreakfast(page: Page, dayOfWeek: number, recipeName: string, rowIndex = 0): Promise<void> {
  await cell(page, dayOfWeek, rowIndex).click();
  await page.getByLabel('Search recipes…').fill(recipeName);
  await page.getByRole('button', { name: new RegExp(`^Add ${recipeName} to the selected meal$`) }).click();
  await expect(cell(page, dayOfWeek, rowIndex)).toContainText(recipeName);
}

async function waitForSave(page: Page): Promise<void> {
  const put = page.waitForResponse(
    (response) => response.url().includes('/nutrition/plan-templates/') && response.request().method() === 'PUT',
  );
  await page.getByRole('button', { name: 'Save' }).click();
  expect((await put).ok()).toBe(true);
  await expect(page.getByTestId('save-status')).toHaveText('Saved');
}

/**
 * Monday breakfast holds `first`, Tuesday lunch holds `second`: a filled tile in another row on another
 * day, so a drop between them is a diagonal copy that asks Replace or Add (a same-row drop moves instead). Saved afterwards.
 */
async function seedTwoMeals(page: Page, first: string, second: string): Promise<void> {
  await addRecipeToBreakfast(page, 1, first);
  await addRecipeToBreakfast(page, 2, second, 1);
  await waitForSave(page);
}

test.describe('plan template editor interactions', () => {
  test('dragging a meal card onto an empty cell copies it, and one Undo takes it back', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const stamp = Date.now();
    const recipeName = `QA Drag Recipe ${stamp}`;
    const recipeId = await createRecipe(origin, recipeName);
    const templateId = await createTemplate(origin, `QA Drag Empty ${stamp}`);
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await addRecipeToBreakfast(page, 1, recipeName);
      await waitForSave(page);

      await dragTo(page, cell(page, 1, 0), cell(page, 3, 1));
      await expect(cell(page, 3, 1)).toContainText(recipeName);
      await expect(cell(page, 1, 0)).toContainText(recipeName);
      await expect(page.getByTestId('card-drop-dialog')).toHaveCount(0);
      await expect(page.getByTestId('save-status')).toHaveText('Unsaved changes');

      await page.getByRole('button', { name: 'Undo' }).click();
      await expect(cell(page, 3, 1)).toContainText('Empty');
      await expect(cell(page, 1, 0)).toContainText(recipeName);
      await expect(page.getByTestId('save-status')).toHaveText('Saved');
    } finally {
      await deleteTemplate(origin, templateId);
      await deleteRecipe(origin, recipeId);
    }
  });

  test('dropping onto a filled cell in another row asks first: Cancel changes nothing, Replace and Add are one Undo step each', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const stamp = Date.now();
    const sourceName = `QA Drag Source ${stamp}`;
    const targetName = `QA Drag Target ${stamp}`;
    const sourceId = await createRecipe(origin, sourceName);
    const targetId = await createRecipe(origin, targetName);
    const templateId = await createTemplate(origin, `QA Drag Filled ${stamp}`);
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await seedTwoMeals(page, sourceName, targetName);
      const dialog = page.getByTestId('card-drop-dialog');

      // Cancel: the cell keeps its food and no edit is recorded.
      await dragTo(page, cell(page, 1, 0), cell(page, 2, 1));
      await expect(dialog).toBeVisible();
      await expect(dialog.getByRole('heading')).toContainText('Tuesday');
      await dialog.getByRole('button', { name: 'Cancel' }).click();
      await expect(dialog).toHaveCount(0);
      await expect(cell(page, 2, 1)).toContainText(targetName);
      await expect(cell(page, 2, 1)).not.toContainText(sourceName);
      await expect(page.getByTestId('save-status')).toHaveText('Saved');

      // Replace: the target holds only the dropped meal; one Undo restores it exactly.
      await dragTo(page, cell(page, 1, 0), cell(page, 2, 1));
      await dialog.getByRole('button', { name: 'Replace', exact: true }).click();
      await expect(dialog).toHaveCount(0);
      await expect(cell(page, 2, 1)).toContainText(sourceName);
      await expect(cell(page, 2, 1)).not.toContainText(targetName);
      await page.getByRole('button', { name: 'Undo' }).click();
      await expect(cell(page, 2, 1)).toContainText(targetName);
      await expect(cell(page, 2, 1)).not.toContainText(sourceName);
      await expect(page.getByTestId('save-status')).toHaveText('Saved');

      // Add to existing: the target holds both; one Undo takes the added meal away again.
      await dragTo(page, cell(page, 1, 0), cell(page, 2, 1));
      await dialog.getByRole('button', { name: 'Add to existing', exact: true }).click();
      await expect(cell(page, 2, 1)).toContainText(targetName);
      await expect(cell(page, 2, 1)).toContainText(sourceName);
      await page.getByRole('button', { name: 'Undo' }).click();
      await expect(cell(page, 2, 1)).not.toContainText(sourceName);
      await expect(page.getByTestId('save-status')).toHaveText('Saved');
    } finally {
      await deleteTemplate(origin, templateId);
      await deleteRecipe(origin, sourceId);
      await deleteRecipe(origin, targetId);
    }
  });

  test('dropping a card on its own cell or outside the grid does nothing', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const stamp = Date.now();
    const recipeName = `QA Drag Noop ${stamp}`;
    const recipeId = await createRecipe(origin, recipeName);
    const templateId = await createTemplate(origin, `QA Drag Noop ${stamp}`);
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await addRecipeToBreakfast(page, 1, recipeName);
      await waitForSave(page);

      await dragTo(page, cell(page, 1, 0), cell(page, 1, 0));
      await dragTo(page, cell(page, 1, 0), libraryPanel(page).getByRole('tablist', { name: 'Recipes and ingredients' }));
      await expect(page.getByTestId('card-drop-dialog')).toHaveCount(0);
      await expect(page.getByTestId('save-status')).toHaveText('Saved');
      await expect(cell(page, 1, 0)).toContainText(recipeName);
    } finally {
      await deleteTemplate(origin, templateId);
      await deleteRecipe(origin, recipeId);
    }
  });

  test('Enter on a filled cell still opens the meal detail, 560 px wide with the dot-style macros', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const stamp = Date.now();
    const recipeName = `QA Detail Recipe ${stamp}`;
    const recipeId = await createRecipe(origin, recipeName);
    const templateId = await createTemplate(origin, `QA Detail ${stamp}`);
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await addRecipeToBreakfast(page, 1, recipeName);

      await cell(page, 1, 0).focus();
      await page.keyboard.press('Enter');
      const detail = page.getByTestId('meal-detail');
      await expect(detail).toBeVisible();
      // The popover scales in over ~150 ms; measure once the width has settled.
      await expect.poll(async () => (await detail.boundingBox())?.width ?? 0).toBeGreaterThan(550);
      const box = await detail.boundingBox();
      expect(box?.width ?? 0).toBeLessThanOrEqual(560.5);

      // Each item row and the totals show P / C / F / Fib with a colour dot.
      await expect(detail.getByTestId('meal-item').first()).toContainText(/P \d+/);
      await expect(detail.getByTestId('meal-item').first()).toContainText(/Fib \d+/);
      await expect(detail.getByText(/Fib \d+ g/)).toBeVisible();
    } finally {
      await deleteTemplate(origin, templateId);
      await deleteRecipe(origin, recipeId);
    }
  });

  test('items reorder by drag inside the meal popover, within their own list, and one Undo restores it', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const stamp = Date.now();
    const firstName = `QA Order First ${stamp}`;
    const secondName = `QA Order Second ${stamp}`;
    const firstId = await createRecipe(origin, firstName);
    const secondId = await createRecipe(origin, secondName);
    const templateId = await createTemplate(origin, `QA Order ${stamp}`);
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await addRecipeToBreakfast(page, 1, firstName);
      await page.getByLabel('Search recipes…').fill(secondName);
      await page.getByRole('button', { name: new RegExp(`^Add ${secondName} to the selected meal$`) }).click();
      await libraryPanel(page).getByRole('tab', { name: 'Ingredients' }).click();
      await page.getByLabel('Search ingredients…').fill('Apple');
      // Wait for the filtered list: every card is an Apple row before the first Add is pressed.
      const ingredientCards = libraryPanel(page).getByTestId('library-card');
      await expect(ingredientCards.first()).toContainText('Apple');
      await expect(ingredientCards.filter({ hasNotText: 'Apple' })).toHaveCount(0);
      await ingredientCards
        .first()
        .getByRole('button', { name: /^Add .* to the selected meal$/ })
        .click();
      await waitForSave(page);

      const detail = page.getByTestId('meal-detail');
      const itemNames = async () => (await detail.getByTestId('meal-item').allInnerTexts()).map((text) => text.split('\n')[0]);
      await cell(page, 1, 0).click();
      await expect(detail).toBeVisible();
      await expect.poll(itemNames).toEqual([firstName, secondName, expect.stringContaining('Apple')]);

      // An ingredient dropped on the recipe list stays where it is.
      await dragTo(page, detail.getByTestId('meal-item').nth(2), detail.getByTestId('meal-item').nth(0));
      await expect.poll(itemNames).toEqual([firstName, secondName, expect.stringContaining('Apple')]);
      await expect(page.getByTestId('save-status')).toHaveText('Saved');

      // The second recipe moves above the first; recipes stay above ingredients.
      await dragTo(page, detail.getByTestId('meal-item').nth(1), detail.getByTestId('meal-item').nth(0));
      await expect.poll(itemNames).toEqual([secondName, firstName, expect.stringContaining('Apple')]);
      await expect(page.getByTestId('save-status')).toHaveText('Unsaved changes');

      await waitForSave(page);
      await page.reload();
      await cell(page, 1, 0).click();
      await expect.poll(itemNames).toEqual([secondName, firstName, expect.stringContaining('Apple')]);

      // Undo is one step: it restores the original order.
      await dragTo(page, detail.getByTestId('meal-item').nth(1), detail.getByTestId('meal-item').nth(0));
      await expect.poll(itemNames).toEqual([firstName, secondName, expect.stringContaining('Apple')]);
      await page.keyboard.press('Escape');
      await expect(detail).toHaveCount(0);
      await page.getByRole('button', { name: 'Undo' }).click();
      await cell(page, 1, 0).click();
      await expect.poll(itemNames).toEqual([secondName, firstName, expect.stringContaining('Apple')]);
      await expect(page.getByTestId('save-status')).toHaveText('Saved');
    } finally {
      await deleteTemplate(origin, templateId);
      await deleteRecipe(origin, firstId);
      await deleteRecipe(origin, secondId);
    }
  });

  test('a library card opens an info popover with the ingredients and no steps; + and drag still work', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const stamp = Date.now();
    const recipeName = `QA Info Recipe ${stamp}`;
    const recipeId = await createRecipe(origin, recipeName);
    const templateId = await createTemplate(origin, `QA Info ${stamp}`);
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await page.getByLabel('Search recipes…').fill(recipeName);
      const card = libraryCard(page, recipeName);
      await expect(card).toHaveCount(1);

      // No picture: the chef-hat placeholder tile, and the macros as dots.
      await expect(card.locator('svg.lucide-chef-hat')).toHaveCount(1);
      await expect(card).toContainText(/P \d+/);

      await card.getByRole('button', { name: `Show details of ${recipeName}` }).click();
      const info = page.getByTestId('library-info');
      await expect(info).toBeVisible();
      await expect(info).toContainText('Recipe · per portion · 2 portions');
      await expect(info).toContainText('kcal per portion');
      await expect(info.getByTestId('library-info-ingredient')).toHaveCount(1);
      await expect(info.getByTestId('library-info-ingredient')).toContainText('100 g');
      await expect(info).not.toContainText('Whisk the secret sauce.');

      await info.getByRole('button', { name: 'Close' }).click();
      await expect(info).toHaveCount(0);

      // + adds to the selected meal without opening the popover.
      await cell(page, 1, 0).click();
      await card.getByRole('button', { name: `Add ${recipeName} to the selected meal` }).click();
      await expect(cell(page, 1, 0)).toContainText(recipeName);
      await expect(info).toHaveCount(0);

      // Press and move still drags the whole card.
      await dragTo(page, card, cell(page, 2, 1));
      await expect(cell(page, 2, 1)).toContainText(recipeName);
      await expect(info).toHaveCount(0);
    } finally {
      await deleteTemplate(origin, templateId);
      await deleteRecipe(origin, recipeId);
    }
  });

  test('the info popover of an ingredient shows values per 100 g and no ingredient list', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const templateId = await createTemplate(origin, `QA Info Ingredient ${Date.now()}`);
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await libraryPanel(page).getByRole('tab', { name: 'Ingredients' }).click();
      await page.getByLabel('Search ingredients…').fill('Apple');
      const card = libraryPanel(page).getByTestId('library-card').first();
      await expect(card.locator('svg.lucide-apple')).toHaveCount(1);
      await card.getByRole('button', { name: /^Show details of / }).click();

      const info = page.getByTestId('library-info');
      await expect(info).toBeVisible();
      await expect(info).toContainText('Ingredient · per 100 g');
      await expect(info).toContainText('kcal per 100 g');
      await expect(info.getByTestId('library-info-ingredient')).toHaveCount(0);
      await expect(info.getByText('Ingredients · per portion')).toHaveCount(0);

      await page.keyboard.press('Escape');
      await expect(info).toHaveCount(0);
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('a recipe whose detail is gone still shows its summary and says the ingredients are unavailable', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const stamp = Date.now();
    const recipeName = `QA Info Gone ${stamp}`;
    const recipeId = await createRecipe(origin, recipeName);
    const templateId = await createTemplate(origin, `QA Info Gone ${stamp}`);
    try {
      // The recipe stays in the list; only its detail request answers 404, as after a delete elsewhere.
      await page.route(`**/recipes/${recipeId}`, (route) =>
        route.request().method() === 'GET'
          ? route.fulfill({ status: 404, contentType: 'application/json', body: '{"status":404}' })
          : route.continue(),
      );
      await page.goto(`/plan-templates/${templateId}`);
      await page.getByLabel('Search recipes…').fill(recipeName);
      const card = libraryCard(page, recipeName);
      await expect(card).toHaveCount(1);

      await card.getByRole('button', { name: `Show details of ${recipeName}` }).click();
      const info = page.getByTestId('library-info');
      await expect(info).toBeVisible();
      await expect(info).toContainText(recipeName);
      await expect(info).toContainText('kcal per portion');
      await expect(info).toContainText('The ingredients are unavailable');
    } finally {
      await deleteTemplate(origin, templateId);
      await deleteRecipe(origin, recipeId);
    }
  });

  test('the meal popover on a bottom-row cell never covers the fixed header, so Save stays clickable', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const stamp = Date.now();
    const recipeName = `QA Popover Bounds ${stamp}`;
    const recipeId = await createRecipe(origin, recipeName);
    const templateId = await createTemplate(origin, `QA Popover Bounds ${stamp}`);
    try {
      await page.setViewportSize({ width: 1280, height: 720 });
      await page.goto(`/plan-templates/${templateId}`);
      await cell(page, 4, 1).click();
      await page.getByLabel('Search recipes…').fill(recipeName);
      await page.getByRole('button', { name: new RegExp(`^Add ${recipeName} to the selected meal$`) }).click();
      await expect(cell(page, 4, 1)).toContainText(recipeName);

      await cell(page, 4, 1).click();
      const detail = page.getByTestId('meal-detail');
      await expect(detail).toBeVisible();
      const header = await page.getByTestId('editor-fixed-header').boundingBox();
      const popover = await detail.boundingBox();
      expect((popover?.y ?? 0) + 1).toBeGreaterThanOrEqual((header?.y ?? 0) + (header?.height ?? 0));
      // A trial click checks that nothing covers the button without pressing it.
      await page.getByRole('button', { name: 'Save' }).click({ trial: true });
    } finally {
      await deleteTemplate(origin, templateId);
      await deleteRecipe(origin, recipeId);
    }
  });

  test('a day note typed in the Week view shows in the Day view, and Undo removes it', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const templateId = await createTemplate(origin, `QA Day Note ${Date.now()}`);
    const note = 'Cook the rice the night before';
    try {
      await page.goto(`/plan-templates/${templateId}`);
      const noteButton = page.getByRole('button', { name: 'Edit note for Monday' });
      await expect(noteButton).toHaveAttribute('data-has-note', 'false');

      await noteButton.click();
      const popover = page.getByTestId('day-note-popover');
      await popover.getByLabel('Day note for Monday').fill(note);
      await page.keyboard.press('Escape');
      await expect(popover).toHaveCount(0);
      await expect(noteButton).toHaveAttribute('data-has-note', 'true');
      await expect(page.getByTestId('save-status')).toHaveText('Unsaved changes');

      await page.getByRole('button', { name: 'Day', exact: true }).click();
      // A day with a note shows it as a second line inside the day card and disables the note button.
      const dayNote = page.getByTestId('day-summary').getByRole('textbox', { name: 'Day note', exact: true });
      await expect(dayNote).toHaveValue(note);
      await expect(page.getByTestId('day-note-toggle')).toBeDisabled();

      await page.getByRole('button', { name: 'Undo' }).click();
      await expect(dayNote).toHaveCount(0);
      await expect(page.getByTestId('day-note-toggle')).toBeEnabled();

      // With no note the card is one row; Add day note opens the empty line and focuses it.
      await page.getByTestId('day-note-toggle').click();
      await expect(dayNote).toBeFocused();
      await dayNote.fill('abc');
      await dayNote.fill('');
      await dayNote.blur();
      await expect(dayNote).toHaveCount(0);
      await expect(page.getByTestId('day-note-toggle')).toBeEnabled();

      // The cross removes a note in place.
      await page.getByTestId('day-note-toggle').click();
      await dayNote.fill(note);
      await page.getByTestId('day-note-remove').click();
      await expect(dayNote).toHaveCount(0);
      await expect(page.getByTestId('day-note-toggle')).toBeEnabled();
      await page.getByRole('button', { name: 'Week', exact: true }).click();
      await expect(page.getByRole('button', { name: 'Edit note for Monday' })).toHaveAttribute('data-has-note', 'false');
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('switching to the Nutrition view and back leaves the library open', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const templateId = await createTemplate(origin, `QA Library Open ${Date.now()}`);
    try {
      await page.goto(`/plan-templates/${templateId}`);
      const library = libraryPanel(page);
      await expect(library.getByRole('button', { name: 'Hide library' })).toBeVisible();

      await page.getByRole('button', { name: 'Nutrition', exact: true }).click();
      await expect(page.getByTestId('nutrition-cell').first()).toBeVisible();
      await expect(library.getByRole('button', { name: 'Hide library' })).toBeVisible();
      await expect(library.getByLabel('Search recipes…')).toBeVisible();

      await page.getByRole('button', { name: 'Meals', exact: true }).click();
      await expect(library.getByRole('button', { name: 'Hide library' })).toBeVisible();

      // A library the maintainer collapsed stays collapsed across the switch too.
      await library.getByRole('button', { name: 'Hide library' }).click();
      await page.getByRole('button', { name: 'Nutrition', exact: true }).click();
      await expect(page.getByRole('button', { name: 'Show library' })).toBeVisible();
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });
});
