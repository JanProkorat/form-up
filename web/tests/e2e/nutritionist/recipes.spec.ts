/**
 * Recipes page (epic #1052, page 7): list, create with ingredients and
 * preparation steps, round-trip after reload, edit, delete with confirmation,
 * the match-ALL dietary-preference filter, the empty state (stubbed — the QA
 * seed always has recipes), picture upload/remove, and the trainer-only
 * "nutritionists only" state.
 *
 * Lives under nutritionist/ because every /recipes endpoint is
 * Nutritionist-only. The trainer-only test imports `trainerTest` directly —
 * project routing is by file path, not by which fixture a test uses (same
 * pattern as ingredients.spec.ts).
 */
import { request as apiRequest, type Locator, type Page } from '@playwright/test';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { nutritionistTest as test, trainerTest, expect } from '../fixtures/auth';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const IMAGE_A_PATH = path.resolve(__dirname, '..', 'fixtures', 'ingredient-picture-a.png');

// Presigned upload URLs target the host's MinIO port, so uploads work only in a host-run browser,
// which cannot resolve the internal minio-test host for stored pictures (routed below).
const IN_CONTAINER = process.env['PLAYWRIGHT_IN_CONTAINER'] === 'true';

const NUTRITIONIST_EMAIL = 'qa.nutri@formup.test';

interface LoginResponseBody {
  accessToken: string;
}

interface FoodApiBody {
  foodId?: string;
  name?: string;
  isSystem?: boolean;
}

interface SearchFoodsApiBody {
  foods?: FoodApiBody[];
}

interface RecipeApiBody {
  recipeId?: string;
  visibility?: string;
  imageUrl?: string | null;
  galleryImageUrls?: string[] | null;
}

async function loginAsNutritionist(baseURL: string): Promise<string> {
  const password = process.env['QA_SEED_PASSWORD'];
  if (!password) {
    throw new Error('[recipes] QA_SEED_PASSWORD is not set. Copy .env.test.example to .env.test and fill it in.');
  }
  const api = await apiRequest.newContext({ baseURL });
  try {
    const response = await api.post('/auth/login', { data: { email: NUTRITIONIST_EMAIL, password } });
    if (!response.ok()) {
      throw new Error(`[recipes] login as qa.nutri returned ${response.status()} ${response.statusText()}.`);
    }
    return ((await response.json()) as LoginResponseBody).accessToken;
  } finally {
    await api.dispose();
  }
}

/** Finds a system food's id via GET /foods/search, for fixture recipes created through the API. */
async function findFoodId(baseURL: string, accessToken: string): Promise<string> {
  const api = await apiRequest.newContext({ baseURL });
  try {
    const response = await api.get('/foods/search?q=Apple', { headers: { Authorization: `Bearer ${accessToken}` } });
    const { foods } = (await response.json()) as SearchFoodsApiBody;
    const food = (foods ?? []).find((candidate) => candidate.foodId);
    if (!food?.foodId) {
      throw new Error('[recipes] GET /foods/search?q=Apple returned no food.');
    }
    return food.foodId;
  } finally {
    await api.dispose();
  }
}

/** Creates a recipe through the API and returns its id. */
async function createRecipeViaApi(
  baseURL: string,
  body: { name: string; dietaryPreferences: string[] },
): Promise<string> {
  const accessToken = await loginAsNutritionist(baseURL);
  const foodId = await findFoodId(baseURL, accessToken);
  const api = await apiRequest.newContext({ baseURL });
  try {
    const response = await api.post('/recipes', {
      data: {
        name: body.name,
        servings: 2,
        mealTypes: ['Lunch'],
        dietaryPreferences: body.dietaryPreferences,
        foods: [{ foodExternalId: foodId, amountGrams: 100 }],
        steps: [{ description: 'Mix.' }],
      },
      headers: { Authorization: `Bearer ${accessToken}` },
    });
    if (!response.ok()) {
      throw new Error(`[recipes] POST /recipes returned ${response.status()} ${response.statusText()}.`);
    }
    const created = (await response.json()) as RecipeApiBody;
    if (!created.recipeId) {
      throw new Error('[recipes] POST /recipes returned no recipeId.');
    }
    return created.recipeId;
  } finally {
    await api.dispose();
  }
}

async function deleteRecipeViaApi(baseURL: string, recipeId: string): Promise<void> {
  const accessToken = await loginAsNutritionist(baseURL);
  const api = await apiRequest.newContext({ baseURL });
  try {
    await api.delete(`/recipes/${recipeId}`, { headers: { Authorization: `Bearer ${accessToken}` } });
  } finally {
    await api.dispose();
  }
}

async function fetchRecipePictures(
  baseURL: string,
  recipeId: string,
): Promise<{ imageUrl: string | null; galleryImageUrls: string[] }> {
  const accessToken = await loginAsNutritionist(baseURL);
  const api = await apiRequest.newContext({ baseURL });
  try {
    const response = await api.get(`/recipes/${recipeId}`, { headers: { Authorization: `Bearer ${accessToken}` } });
    const body = (await response.json()) as RecipeApiBody;
    return { imageUrl: body.imageUrl ?? null, galleryImageUrls: body.galleryImageUrls ?? [] };
  } finally {
    await api.dispose();
  }
}

async function fetchRecipeVisibility(baseURL: string, recipeId: string): Promise<string | undefined> {
  const accessToken = await loginAsNutritionist(baseURL);
  const api = await apiRequest.newContext({ baseURL });
  try {
    const response = await api.get(`/recipes/${recipeId}`, { headers: { Authorization: `Bearer ${accessToken}` } });
    return ((await response.json()) as RecipeApiBody).visibility;
  } finally {
    await api.dispose();
  }
}

async function fetchRecipeImageUrl(baseURL: string, recipeId: string): Promise<string | null> {
  return (await fetchRecipePictures(baseURL, recipeId)).imageUrl;
}

/** Finds one of the caller's recipes by its exact name, or `undefined` when there is none. */
async function findRecipeIdByName(baseURL: string, name: string): Promise<string | undefined> {
  const accessToken = await loginAsNutritionist(baseURL);
  const api = await apiRequest.newContext({ baseURL });
  try {
    const response = await api.get(`/recipes?search=${encodeURIComponent(name)}&page=1&pageSize=10`, {
      headers: { Authorization: `Bearer ${accessToken}` },
    });
    const body = (await response.json()) as { recipes?: (RecipeApiBody & { name?: string })[] };
    return body.recipes?.find((recipe) => recipe.name === name)?.recipeId;
  } finally {
    await api.dispose();
  }
}

/** Recipes opens as a card grid; the table-based tests pin the list view before navigating. */
async function useListView(page: Page): Promise<void> {
  await page.addInitScript(() => window.localStorage.setItem('recipes-view-mode', 'list'));
}

test.describe('recipes page', () => {
  test.beforeEach(async ({ page }) => {
    await useListView(page);
    await page.goto('/recipes');
    await page.waitForLoadState('networkidle');
    await expect(page.getByRole('heading', { name: 'Recipes', exact: true })).toBeVisible();
  });

  test('list loads with a table, per-serving columns and a pagination footer', async ({ page }) => {
    await expect(page.getByText(/Viewing \d+ of \d+/)).toBeVisible();
    await expect(page.getByRole('columnheader', { name: 'Name' })).toBeVisible();
    await expect(page.getByRole('columnheader', { name: 'Calories / serving' })).toBeVisible();
    await expect(page.getByRole('columnheader', { name: 'Library' })).toBeVisible();
    await expect(page.locator('tbody tr').first()).toBeVisible();
  });

  test('create with ingredients and steps, reload round-trip, edit, then delete', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const name = `QA Recipe ${Date.now()}`;
    const drawer = page.locator('[data-slot="sheet-content"]');

    await page.getByRole('button', { name: 'New Recipe' }).click();
    await expect(drawer.getByRole('heading', { name: 'New Recipe' })).toBeVisible();

    // Details tab.
    await drawer.getByLabel(/^Name\b/).fill(name);
    await drawer.getByLabel(/^Servings\b/).fill('2');
    await drawer.locator('#recipe-meal-types').click();
    await page.getByRole('checkbox', { name: 'Lunch' }).click();
    await page.keyboard.press('Escape');

    // Ingredients tab: search a food, add it, set grams.
    await drawer.getByRole('tab', { name: /Ingredients/ }).click();
    await drawer.getByRole('searchbox', { name: 'Search ingredients to add' }).fill('Apple');
    await drawer.getByRole('button', { name: /Apple/ }).first().click();
    const amount = drawer.getByLabel(/^Amount in grams for/);
    await amount.fill('200');
    await expect(drawer.getByTestId('recipe-macro-summary')).toContainText('Per serving (2)');

    // Preparation tab: two steps.
    await drawer.getByRole('tab', { name: /Preparation/ }).click();
    await drawer.getByRole('button', { name: '+ Add step' }).click();
    await drawer.getByLabel('Step 1', { exact: true }).fill('Wash the apple.');
    await drawer.getByRole('button', { name: '+ Add step' }).click();
    await drawer.getByLabel('Step 2', { exact: true }).fill('Slice and serve.');

    const createResponse = page.waitForResponse(
      (response) => response.url().includes('/recipes') && response.request().method() === 'POST',
    );
    await drawer.getByRole('button', { name: 'Save Recipe' }).click();
    expect((await createResponse).status()).toBe(201);
    await expect(drawer).toHaveCount(0);

    let recipeId: string | undefined;
    try {
      // Reload and find it again: the round trip comes from the server, not local state.
      await page.reload();
      await page.getByPlaceholder('Search recipes…').fill(name);
      await expect(page.getByRole('cell', { name })).toBeVisible();
      await expect(page.getByRole('row', { name }).getByText('Mine', { exact: true })).toBeVisible();

      await page.getByRole('cell', { name }).click();
      await expect(drawer.getByRole('heading', { name: 'Edit Recipe' })).toBeVisible();
      await expect(drawer.getByLabel(/^Name\b/)).toHaveValue(name);
      await expect(drawer.getByLabel(/^Servings\b/)).toHaveValue('2');
      await drawer.getByRole('tab', { name: /Ingredients/ }).click();
      await expect(drawer.getByLabel(/^Amount in grams for/)).toHaveValue('200');
      await drawer.getByRole('tab', { name: /Preparation/ }).click();
      await expect(drawer.getByLabel('Step 1', { exact: true })).toHaveValue('Wash the apple.');
      await expect(drawer.getByLabel('Step 2', { exact: true })).toHaveValue('Slice and serve.');

      // Edit the name.
      await drawer.getByRole('tab', { name: 'Details' }).click();
      const editedName = `${name} edited`;
      await drawer.getByLabel(/^Name\b/).fill(editedName);
      const updateResponse = page.waitForResponse(
        (response) => response.url().includes('/recipes/') && response.request().method() === 'PUT',
      );
      await drawer.getByRole('button', { name: 'Save Recipe' }).click();
      expect((await updateResponse).status()).toBe(200);
      await expect(drawer).toHaveCount(0);

      await page.getByPlaceholder('Search recipes…').fill(editedName);
      await expect(page.getByRole('cell', { name: editedName })).toBeVisible();

      // Delete, with confirmation.
      await page.getByRole('cell', { name: editedName }).click();
      await drawer.getByRole('button', { name: 'Delete' }).click();
      await expect(page.getByRole('heading', { name: 'Delete recipe' })).toBeVisible();
      await page.getByRole('button', { name: 'Delete', exact: true }).last().click();
      await expect(drawer).toHaveCount(0);
      await expect(page.getByRole('cell', { name: editedName })).toHaveCount(0);
    } catch (error) {
      // A failed run must not leave the recipe behind for later runs.
      const accessToken = await loginAsNutritionist(origin);
      const api = await apiRequest.newContext({ baseURL: origin });
      try {
        const search = await api.get(`/recipes?search=${encodeURIComponent(name)}&page=1&pageSize=10`, {
          headers: { Authorization: `Bearer ${accessToken}` },
        });
        const body = (await search.json()) as { recipes?: RecipeApiBody[] };
        recipeId = body.recipes?.[0]?.recipeId;
      } finally {
        await api.dispose();
      }
      if (recipeId) {
        await deleteRecipeViaApi(origin, recipeId);
      }
      throw error;
    }
  });

  test('create mode enables the Pictures tab and rejects a non-image file before staging it', async ({ page }) => {
    const drawer = page.locator('[data-slot="sheet-content"]');
    await page.getByRole('button', { name: 'New Recipe' }).click();
    // The Details tab starts with the name, not a picture field.
    await expect(drawer.getByLabel(/^Name\b/)).toBeVisible();
    await expect(drawer.getByRole('button', { name: 'Upload picture' })).toHaveCount(0);

    await drawer.getByRole('tab', { name: 'Pictures' }).click();
    await expect(drawer.getByRole('button', { name: 'Upload picture' })).toBeVisible();
    await drawer
      .getByTestId('staged-main-input')
      .setInputFiles({ name: 'notes.txt', mimeType: 'text/plain', buffer: Buffer.from('not an image') });
    await expect(
      page.locator('[data-slot="toast-viewport"]').getByText('Only JPEG, PNG or WebP images are supported.'),
    ).toBeVisible();
    await expect(drawer.getByRole('button', { name: 'Upload picture' })).toBeVisible();
    await expect(drawer.getByRole('button', { name: 'Replace picture' })).toHaveCount(0);
    await drawer.getByRole('button', { name: 'Cancel' }).click();
  });

  test('visibility toggle round-trips Private to Public and back', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const name = `QA Visibility ${Date.now()}`;
    const recipeId = await createRecipeViaApi(origin, { name, dietaryPreferences: [] });
    const drawer = page.locator('[data-slot="sheet-content"]');

    async function saveAndAssert(expected: 'Public' | 'Private') {
      const updateResponse = page.waitForResponse(
        (response) => response.url().includes(`/recipes/${recipeId}`) && response.request().method() === 'PUT',
      );
      await drawer.getByRole('button', { name: 'Save Recipe' }).click();
      expect((await updateResponse).status()).toBe(200);
      await expect(drawer).toHaveCount(0);
      expect(await fetchRecipeVisibility(origin, recipeId)).toBe(expected);
    }

    try {
      expect(await fetchRecipeVisibility(origin, recipeId)).toBe('Private');
      await page.getByPlaceholder('Search recipes…').fill(name);
      await page.getByRole('cell', { name }).click();
      await expect(drawer.getByRole('heading', { name: 'Edit Recipe' })).toBeVisible();

      await expect(drawer.getByRole('radio', { name: 'Private' })).toBeChecked();
      await expect(drawer.getByText('Only you can see and use this recipe.')).toBeVisible();
      await drawer.getByRole('radio', { name: 'Public' }).click();
      await expect(drawer.getByText('All coaches can see and use this recipe.')).toBeVisible();
      await saveAndAssert('Public');

      // The owner's badge stays "Mine" whatever the visibility.
      await expect(page.getByRole('row', { name }).getByText('Mine', { exact: true })).toBeVisible();
      await page.getByRole('cell', { name }).click();
      await expect(drawer.getByRole('radio', { name: 'Public' })).toBeChecked();
      await drawer.getByRole('radio', { name: 'Private' }).click();
      await saveAndAssert('Private');
    } finally {
      await deleteRecipeViaApi(origin, recipeId);
    }
  });

  test('a decimal Servings value blocks the save', async ({ page }) => {
    const drawer = page.locator('[data-slot="sheet-content"]');
    await page.getByRole('button', { name: 'New Recipe' }).click();
    await drawer.getByLabel(/^Name\b/).fill('QA Decimal Servings');
    await drawer.getByLabel(/^Servings\b/).fill('1.5');
    await drawer.locator('#recipe-meal-types').click();
    await page.getByRole('checkbox', { name: 'Lunch' }).click();
    await page.keyboard.press('Escape');

    let posted = false;
    page.on('request', (request) => {
      if (request.method() === 'POST' && request.url().includes('/recipes')) {
        posted = true;
      }
    });
    await drawer.getByRole('button', { name: 'Save Recipe' }).click();
    await expect(drawer.getByText('Servings must be a whole number, at least 1.')).toBeVisible();
    expect(posted).toBe(false);
    await drawer.getByRole('button', { name: 'Cancel' }).click();
  });

  test('invalid save sends the coach to the tab with the problem', async ({ page }) => {
    const drawer = page.locator('[data-slot="sheet-content"]');
    await page.getByRole('button', { name: 'New Recipe' }).click();
    await drawer.getByRole('button', { name: 'Save Recipe' }).click();
    await expect(page.getByText('Name is required.')).toBeVisible();
    await expect(page.getByText('Select at least one meal type.')).toBeVisible();

    await drawer.getByLabel(/^Name\b/).fill('QA Incomplete');
    await drawer.locator('#recipe-meal-types').click();
    await page.getByRole('checkbox', { name: 'Lunch' }).click();
    await page.keyboard.press('Escape');
    await drawer.getByRole('button', { name: 'Save Recipe' }).click();
    // No ingredients yet: the drawer jumps to the Ingredients tab with the message.
    await expect(page.getByText('Add at least one ingredient.')).toBeVisible();
    await drawer.getByRole('button', { name: 'Cancel' }).click();
  });

  test('dietary preference filter matches ALL selected preferences', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const prefix = `QA Diet ${Date.now()}`;
    const veganOnly = await createRecipeViaApi(origin, { name: `${prefix} A`, dietaryPreferences: ['Vegan'] });
    const veganAndGlutenFree = await createRecipeViaApi(origin, {
      name: `${prefix} B`,
      dietaryPreferences: ['Vegan', 'GlutenFree'],
    });

    try {
      await page.getByPlaceholder('Search recipes…').fill(prefix);
      await expect(page.getByRole('cell', { name: `${prefix} A` })).toBeVisible();
      await expect(page.getByRole('cell', { name: `${prefix} B` })).toBeVisible();

      await page.getByRole('button', { name: 'Dietary preferences' }).click();
      await page.getByRole('checkbox', { name: 'Vegan', exact: true }).click();
      await expect(page).toHaveURL(/diet=Vegan/);
      await expect(page.getByRole('cell', { name: `${prefix} A` })).toBeVisible();
      await expect(page.getByRole('cell', { name: `${prefix} B` })).toBeVisible();

      await page.getByRole('checkbox', { name: 'Gluten-free' }).click();
      await expect(page.getByRole('cell', { name: `${prefix} B` })).toBeVisible();
      await expect(page.getByRole('cell', { name: `${prefix} A` })).toHaveCount(0);
    } finally {
      await deleteRecipeViaApi(origin, veganOnly);
      await deleteRecipeViaApi(origin, veganAndGlutenFree);
    }
  });

  test('sorting by servings toggles the URL and keeps the table populated', async ({ page }) => {
    await page.getByRole('button', { name: 'Sort by Servings' }).click();
    await expect(page).toHaveURL(/sort=servings-asc/);
    await expect(page.locator('tbody tr').first()).toBeVisible();
    await page.getByRole('button', { name: 'Sort by Servings' }).click();
    await expect(page).toHaveURL(/sort=servings-desc/);
    await page.getByRole('button', { name: 'Sort by Servings' }).click();
    await expect(page).not.toHaveURL(/sort=/);
  });

  test('a system row opens the drawer read-only with a Close button', async ({ page }) => {
    await page.getByRole('button', { name: 'Owner' }).click();
    await page.getByRole('checkbox', { name: 'System' }).click();
    await page.keyboard.press('Escape');
    await expect(page).toHaveURL(/owner=System/);
    // The unfiltered rows stay on screen until the filtered page arrives, so pick
    // a row by its badge rather than by position.
    await page
      .locator('tbody tr')
      .filter({ has: page.getByText('System', { exact: true }) })
      .first()
      .click();

    const drawer = page.locator('[data-slot="sheet-content"]');
    await expect(drawer.getByRole('heading', { name: 'Recipe', exact: true })).toBeVisible();
    await expect(drawer.getByLabel(/^Name\b/)).toBeDisabled();
    await expect(drawer.getByRole('button', { name: 'Save Recipe' })).toHaveCount(0);
    await expect(drawer.getByRole('button', { name: 'Delete' })).toHaveCount(0);
    // Pictures are viewable but carry no upload / remove / set-as-main controls.
    await drawer.getByRole('tab', { name: 'Pictures' }).click();
    await expect(drawer.getByRole('button', { name: 'Upload picture' })).toHaveCount(0);
    await expect(drawer.getByRole('button', { name: 'Add picture' })).toHaveCount(0);
    await expect(drawer.getByRole('button', { name: /^Remove picture/ })).toHaveCount(0);
    await expect(drawer.getByRole('button', { name: /^Set picture .* as main$/ })).toHaveCount(0);
    await drawer.locator('[data-slot="sheet-footer"]').getByRole('button', { name: 'Close' }).click();
    await expect(drawer).toHaveCount(0);
  });
});

test.describe('recipes view mode', () => {
  test('opens as a card grid, switches to the table and remembers the choice after a reload', async ({ page }) => {
    await page.goto('/recipes');
    await expect(page.getByRole('heading', { name: 'Recipes', exact: true })).toBeVisible();
    await expect(page.getByText(/kcal \/ portion/).first()).toBeVisible();
    await expect(page.getByRole('columnheader', { name: 'Name' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Cards' })).toHaveAttribute('aria-pressed', 'true');

    await page.getByRole('button', { name: 'Table' }).click();
    await expect(page.getByRole('columnheader', { name: 'Name' })).toBeVisible();
    await expect(page.locator('tbody tr').first()).toBeVisible();

    await page.reload();
    await expect(page.getByRole('columnheader', { name: 'Name' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Table' })).toHaveAttribute('aria-pressed', 'true');
  });
});

test.describe('recipes empty state', () => {
  test('shows the empty state with a create button when the list is empty', async ({ page }) => {
    // The QA seed always has recipes, so stub the list to reach the empty state.
    await page.route('**/recipes?*', async (route) => {
      if (route.request().method() !== 'GET') {
        await route.continue();
        return;
      }
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ recipes: [], totalCount: 0, page: 1, pageSize: 25 }),
      });
    });
    await page.goto('/recipes');
    await expect(page.getByText('No recipes yet')).toBeVisible();
    await expect(page.getByText('Create your first recipe to get started')).toBeVisible();
    // Matches recipes-state-01.png: search + New Recipe only, no filter pills.
    await expect(page.getByRole('searchbox')).toBeVisible();
    for (const filter of ['Meal type', 'Owner', 'Tags']) {
      await expect(page.getByRole('button', { name: filter })).toHaveCount(0);
    }
    await page.getByRole('button', { name: 'Create Recipe' }).click();
    await expect(page.locator('[data-slot="sheet-content"]').getByRole('heading', { name: 'New Recipe' })).toBeVisible();
  });
});

test.describe('recipe picture', () => {
  test('create with staged pictures uploads them once the recipe is saved', async ({ page, baseURL }) => {
    test.skip(IN_CONTAINER, 'presigned uploads target the host MinIO port');
    const origin = baseURL ?? 'http://localhost:5173';
    const name = `QA Recipe Staged ${Date.now()}`;
    const drawer = page.locator('[data-slot="sheet-content"]');

    try {
      await useListView(page);
      await page.goto('/recipes');
      await page.getByRole('button', { name: 'New Recipe' }).click();
      await expect(drawer.getByRole('heading', { name: 'New Recipe' })).toBeVisible();

      await drawer.getByLabel(/^Name\b/).fill(name);
      await drawer.getByLabel(/^Servings\b/).fill('2');
      await drawer.locator('#recipe-meal-types').click();
      await page.getByRole('checkbox', { name: 'Lunch' }).click();
      await page.keyboard.press('Escape');
      await drawer.getByRole('tab', { name: /Ingredients/ }).click();
      await drawer.getByRole('searchbox', { name: 'Search ingredients to add' }).fill('Apple');
      await drawer.getByRole('button', { name: /Apple/ }).first().click();
      await drawer.getByLabel(/^Amount in grams for/).fill('200');

      // Stage a main picture and three extras; the middle extra is removed again before saving.
      await drawer.getByRole('tab', { name: 'Pictures' }).click();
      await drawer.getByTestId('staged-main-input').setInputFiles(IMAGE_A_PATH);
      await expect(drawer.getByRole('button', { name: 'Replace picture' })).toBeVisible();
      await drawer.getByTestId('recipe-gallery-input').setInputFiles([IMAGE_A_PATH, IMAGE_A_PATH, IMAGE_A_PATH]);
      await expect(drawer.getByTestId('recipe-gallery-tile')).toHaveCount(3);
      await drawer.getByRole('button', { name: 'Remove picture 2' }).click();
      await expect(drawer.getByTestId('recipe-gallery-tile')).toHaveCount(2);
      // Nothing is sent to the server before the recipe is saved.
      expect(await findRecipeIdByName(origin, name)).toBeUndefined();

      const imageRequests: string[] = [];
      page.on('request', (request) => {
        if (/\/recipes\/[^/]+\/image/.test(request.url()) && request.method() === 'POST') {
          imageRequests.push(new URL(request.url()).searchParams.get('slot') ?? '');
        }
      });
      const createResponse = page.waitForResponse(
        (response) => response.url().endsWith('/recipes') && response.request().method() === 'POST',
      );
      await drawer.getByRole('button', { name: 'Save Recipe' }).click();
      expect((await createResponse).status()).toBe(201);
      await expect(drawer).toHaveCount(0);

      // Main first, then the two extras, sequentially.
      await expect.poll(() => imageRequests).toEqual(['main', 'gallery', 'gallery']);
      const recipeId = await findRecipeIdByName(origin, name);
      expect(recipeId).toBeDefined();
      await expect
        .poll(async () => (await fetchRecipePictures(origin, recipeId as string)).galleryImageUrls.length)
        .toBe(2);
      const pictures = await fetchRecipePictures(origin, recipeId as string);
      expect(pictures.imageUrl).toContain(`${recipeId}/main-`);
    } finally {
      const recipeId = await findRecipeIdByName(origin, name);
      if (recipeId) {
        await deleteRecipeViaApi(origin, recipeId);
      }
    }
  });

  test('create with only staged extra pictures promotes the first extra to main', async ({ page, baseURL }) => {
    test.skip(IN_CONTAINER, 'presigned uploads target the host MinIO port');
    const origin = baseURL ?? 'http://localhost:5173';
    const name = `QA Recipe Extras Only ${Date.now()}`;
    const drawer = page.locator('[data-slot="sheet-content"]');

    try {
      await useListView(page);
      await page.goto('/recipes');
      await page.getByRole('button', { name: 'New Recipe' }).click();
      await expect(drawer.getByRole('heading', { name: 'New Recipe' })).toBeVisible();

      await drawer.getByLabel(/^Name\b/).fill(name);
      await drawer.getByLabel(/^Servings\b/).fill('2');
      await drawer.locator('#recipe-meal-types').click();
      await page.getByRole('checkbox', { name: 'Lunch' }).click();
      await page.keyboard.press('Escape');
      await drawer.getByRole('tab', { name: /Ingredients/ }).click();
      await drawer.getByRole('searchbox', { name: 'Search ingredients to add' }).fill('Apple');
      await drawer.getByRole('button', { name: /Apple/ }).first().click();
      await drawer.getByLabel(/^Amount in grams for/).fill('200');

      // Stage two extras and no main picture.
      await drawer.getByRole('tab', { name: 'Pictures' }).click();
      await drawer.getByTestId('recipe-gallery-input').setInputFiles([IMAGE_A_PATH, IMAGE_A_PATH]);
      await expect(drawer.getByTestId('recipe-gallery-tile')).toHaveCount(2);

      const imageRequests: string[] = [];
      page.on('request', (request) => {
        if (/\/recipes\/[^/]+\/image/.test(request.url()) && request.method() === 'POST') {
          imageRequests.push(new URL(request.url()).searchParams.get('slot') ?? '');
        }
      });
      const createResponse = page.waitForResponse(
        (response) => response.url().endsWith('/recipes') && response.request().method() === 'POST',
      );
      await drawer.getByRole('button', { name: 'Save Recipe' }).click();
      expect((await createResponse).status()).toBe(201);
      await expect(drawer).toHaveCount(0);

      // The first extra becomes the main picture; the other one stays in the gallery.
      await expect.poll(() => imageRequests).toEqual(['main', 'gallery']);
      const recipeId = await findRecipeIdByName(origin, name);
      expect(recipeId).toBeDefined();
      await expect
        .poll(async () => (await fetchRecipePictures(origin, recipeId as string)).galleryImageUrls.length)
        .toBe(1);
      const pictures = await fetchRecipePictures(origin, recipeId as string);
      expect(pictures.imageUrl).toContain(`${recipeId}/main-`);
    } finally {
      const recipeId = await findRecipeIdByName(origin, name);
      if (recipeId) {
        await deleteRecipeViaApi(origin, recipeId);
      }
    }
  });

  test('upload and remove the main picture', async ({ page, baseURL }) => {
    test.skip(IN_CONTAINER, 'presigned uploads target the host MinIO port');
    const origin = baseURL ?? 'http://localhost:5173';
    const name = `QA Recipe Picture ${Date.now()}`;
    const recipeId = await createRecipeViaApi(origin, { name, dietaryPreferences: [] });

    try {
      await page.route('http://minio-test:9000/**', (route) =>
        route.fulfill({ path: IMAGE_A_PATH, contentType: 'image/png' }),
      );
      const naturalWidth = (locator: Locator) =>
        locator.evaluate((element) => (element as HTMLImageElement).naturalWidth);

      await useListView(page);
      await page.goto('/recipes');
      await page.getByPlaceholder('Search recipes…').fill(name);
      await page.getByRole('cell', { name }).click();
      const drawer = page.locator('[data-slot="sheet-content"]');
      await expect(drawer.getByRole('heading', { name: 'Edit Recipe' })).toBeVisible();
      await drawer.getByRole('tab', { name: 'Pictures' }).click();
      await expect(drawer.getByRole('button', { name: 'Upload picture' })).toBeVisible();

      const confirmResponse = page.waitForResponse(
        (response) =>
          response.url().includes(`/recipes/${recipeId}/image`) && response.request().method() === 'PUT',
      );
      // The main picture's input comes first in the DOM; the gallery has its own test id.
      await drawer.locator('input[type="file"]').first().setInputFiles(IMAGE_A_PATH);
      expect((await confirmResponse).status()).toBe(204);
      await expect.poll(() => naturalWidth(drawer.getByAltText('Recipe picture', { exact: true }))).toBe(64);
      expect(await fetchRecipeImageUrl(origin, recipeId)).toContain(recipeId);

      const removeResponse = page.waitForResponse(
        (response) =>
          response.url().includes(`/recipes/${recipeId}/image`) && response.request().method() === 'DELETE',
      );
      await drawer.getByRole('button', { name: 'Remove picture', exact: true }).click();
      await page.getByRole('button', { name: 'Remove', exact: true }).click();
      expect((await removeResponse).status()).toBe(204);
      await expect(drawer.getByRole('button', { name: 'Upload picture' })).toBeVisible();
      expect(await fetchRecipeImageUrl(origin, recipeId)).toBeNull();
    } finally {
      await deleteRecipeViaApi(origin, recipeId);
    }
  });

  test('add an extra picture, set it as main, then remove an extra', async ({ page, baseURL }) => {
    test.skip(IN_CONTAINER, 'presigned uploads target the host MinIO port');
    const origin = baseURL ?? 'http://localhost:5173';
    const name = `QA Recipe Gallery ${Date.now()}`;
    const recipeId = await createRecipeViaApi(origin, { name, dietaryPreferences: [] });

    try {
      await page.route('http://minio-test:9000/**', (route) =>
        route.fulfill({ path: IMAGE_A_PATH, contentType: 'image/png' }),
      );

      await useListView(page);
      await page.goto('/recipes');
      await page.getByPlaceholder('Search recipes…').fill(name);
      await page.getByRole('cell', { name }).click();
      const drawer = page.locator('[data-slot="sheet-content"]');
      await expect(drawer.getByRole('heading', { name: 'Edit Recipe' })).toBeVisible();
      await drawer.getByRole('tab', { name: 'Pictures' }).click();

      const isImagePut = (slot: string, method: string) => (response: { url(): string; request(): { method(): string } }) =>
        response.url().includes(`/recipes/${recipeId}/image`) &&
        response.url().includes(`slot=${slot}`) &&
        response.request().method() === method;

      // Main picture first, so the swap below has a main to hand over.
      const mainConfirm = page.waitForResponse(isImagePut('main', 'PUT'));
      await drawer.locator('input[type="file"]').first().setInputFiles(IMAGE_A_PATH);
      expect((await mainConfirm).status()).toBe(204);
      await expect(drawer.getByRole('button', { name: 'Replace picture' })).toBeVisible();

      // Add an extra picture.
      const galleryConfirm = page.waitForResponse(isImagePut('gallery', 'PUT'));
      await drawer.getByTestId('recipe-gallery-input').setInputFiles(IMAGE_A_PATH);
      expect((await galleryConfirm).status()).toBe(204);
      await expect(drawer.getByTestId('recipe-gallery-tile')).toHaveCount(1);
      const afterAdd = await fetchRecipePictures(origin, recipeId);
      expect(afterAdd.imageUrl).toContain(`${recipeId}/main-`);
      expect(afterAdd.galleryImageUrls).toHaveLength(1);
      expect(afterAdd.galleryImageUrls[0]).toContain(`${recipeId}/gallery-`);

      // Set the extra as main: the old main takes its slot.
      const promoteResponse = page.waitForResponse(
        (response) =>
          response.url().includes(`/recipes/${recipeId}/gallery/promote`) && response.request().method() === 'POST',
      );
      await drawer.getByRole('button', { name: 'Set picture 1 as main' }).click();
      expect((await promoteResponse).status()).toBe(204);
      await expect.poll(async () => (await fetchRecipePictures(origin, recipeId)).imageUrl).toBe(
        afterAdd.galleryImageUrls[0],
      );
      const afterPromote = await fetchRecipePictures(origin, recipeId);
      expect(afterPromote.galleryImageUrls).toEqual([afterAdd.imageUrl]);
      await expect(drawer.getByTestId('recipe-gallery-tile')).toHaveCount(1);

      // Remove the extra (now the previous main), with confirmation.
      const removeResponse = page.waitForResponse(
        (response) =>
          response.url().includes(`/recipes/${recipeId}/gallery`) && response.request().method() === 'DELETE',
      );
      await drawer.getByRole('button', { name: 'Remove picture 1' }).click();
      await page.getByRole('button', { name: 'Remove', exact: true }).click();
      expect((await removeResponse).status()).toBe(204);
      await expect(drawer.getByTestId('recipe-gallery-tile')).toHaveCount(0);
      expect((await fetchRecipePictures(origin, recipeId)).galleryImageUrls).toEqual([]);
    } finally {
      await deleteRecipeViaApi(origin, recipeId);
    }
  });
});

trainerTest.describe('recipes for a trainer-only coach', () => {
  trainerTest('redirects a trainer-only coach to clients', async ({ page }) => {
    await page.goto('/recipes');
    await expect(page).toHaveURL(/\/clients$/);
    await expect(page.locator('[data-slot="toast-viewport"]').getByRole('status')).toHaveCount(0);
  });
});
