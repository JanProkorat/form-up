/**
 * #1115 — Ingredients page: list load, search, tag filter, create/edit/
 * delete a private ingredient, and a read-only system row. #1120 adds
 * coach-private food tags: create/edit/delete/filter/assign, and a
 * trainer-only coach seeing no tag UI at all.
 *
 * Path is deliberately under nutritionist/ — every project in
 * playwright.config.ts matches a role subfolder via testMatch, and
 * create/edit/delete on `/foods` (and all food-tag CRUD) are
 * Nutritionist-only (`Roles(AppRoles.Nutritionist)`) — the trainer fixture
 * can read the list but cannot exercise any write path. The trainer-only
 * "no tag UI" test below imports `trainerTest` directly rather than
 * living in its own `trainer/` file — Playwright's project routing is by
 * file path via `testMatch`, not by which fixture a test uses, so a single
 * test built on `trainerTest` inside this `nutritionist/` file still runs
 * (only) under the `nutritionist` project, same as every other test here.
 */
import { request as apiRequest, type Locator } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { nutritionistTest as test, trainerTest, expect } from '../fixtures/auth';

// ESM-safe __dirname substitute (package.json has "type":"module") — same
// pattern as `inbox-attachments.spec.ts`.
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const IMAGE_A_PATH = path.resolve(__dirname, '..', 'fixtures', 'ingredient-picture-a.png');
const IMAGE_B_PATH = path.resolve(__dirname, '..', 'fixtures', 'ingredient-picture-b.png');

// In the harness, presigned upload URLs target the host's MinIO port, so uploads work only in a
// host-run browser, which can't resolve the internal minio-test host for stored pictures. The
// upload tests route those reads to the fixture bytes and skip inside the Playwright container.
const IN_CONTAINER = process.env['PLAYWRIGHT_IN_CONTAINER'] === 'true';

const NUTRITIONIST_EMAIL = 'qa.nutri@formup.test';

interface LoginResponseBody {
  accessToken: string;
}

interface NutrientValueApiBody {
  fiber?: number;
  sugar?: number;
  saturatedFat?: number;
  salt?: number;
}

interface ServingApiBody {
  label?: string;
  weightGrams?: number;
}

interface FoodApiBody {
  foodId?: string;
  name?: string;
  rawName?: string;
  isSystem?: boolean;
  visibility?: string;
  tags?: FoodTagApiBody[];
  nutrientValue?: NutrientValueApiBody;
  commonServings?: ServingApiBody[];
}

interface FoodTagApiBody {
  tagId?: string;
  name?: string;
}

interface GetFoodTagsApiBody {
  tags?: FoodTagApiBody[];
}

interface SearchFoodsApiBody {
  foods?: FoodApiBody[];
}

interface MailHogPart {
  Headers?: Record<string, string[]>;
  Body?: string;
  MIME?: { Parts?: MailHogPart[] } | null;
}

/** A MailHog v2 message: a single-part mail has its body under `Content` and `MIME: null`. */
interface MailHogMessage {
  Content?: MailHogPart;
  MIME?: { Parts?: MailHogPart[] } | null;
}

interface MailHogSearchBody {
  items?: MailHogMessage[];
}

/**
 * Finds the MailHog HTTP API the harness mail sink serves. Its host port is
 * ephemeral (docker-compose.test.yml), so: an explicit E2E_MAILHOG_URL wins,
 * the Playwright container reaches it by service name, and a host run asks
 * docker for the mailhog-test container in the same compose project as the
 * API container that publishes E2E_API_URL's port.
 */
function resolveMailHogUrl(): string {
  const explicit = process.env['E2E_MAILHOG_URL'];
  if (explicit) {
    return explicit.replace(/\/$/, '');
  }
  if (IN_CONTAINER) {
    return 'http://mailhog-test:8025';
  }
  const docker = (args: string[]): string => execFileSync('docker', args, { encoding: 'utf8' }).trim();
  const apiPort = new URL(process.env['E2E_API_URL'] ?? 'https://localhost:5101').port;
  const apiContainer = docker(['ps', '--filter', `publish=${apiPort}`, '--format', '{{.ID}}']).split('\n')[0];
  if (!apiContainer) {
    throw new Error(`[ingredients] No docker container publishes API port ${apiPort}; set E2E_MAILHOG_URL.`);
  }
  const project = docker(['inspect', '-f', '{{index .Config.Labels "com.docker.compose.project"}}', apiContainer]);
  const mailContainer = docker([
    'ps',
    '--filter',
    `label=com.docker.compose.project=${project}`,
    '--filter',
    'label=com.docker.compose.service=mailhog-test',
    '--format',
    '{{.ID}}',
  ]).split('\n')[0];
  if (!mailContainer) {
    throw new Error(`[ingredients] No mailhog-test container in compose project ${project}; set E2E_MAILHOG_URL.`);
  }
  const hostPort = docker(['port', mailContainer, '8025']).split('\n')[0]?.split(':').pop();
  return `http://localhost:${hostPort}`;
}

/** Decodes one MIME part's body per its Content-Transfer-Encoding (quoted-printable or base64). */
function decodeMailPart(part: MailHogPart): string {
  const body = part.Body ?? '';
  const encoding = (part.Headers?.['Content-Transfer-Encoding']?.[0] ?? '').toLowerCase();
  if (encoding.includes('base64')) {
    return Buffer.from(body.replace(/\s+/g, ''), 'base64').toString('utf8');
  }
  if (encoding.includes('quoted-printable')) {
    return body
      .replace(/=\r?\n/g, '')
      .replace(/=([0-9A-Fa-f]{2})/g, (_match, hex: string) => String.fromCharCode(parseInt(hex, 16)));
  }
  return body;
}

function collectMailText(part: MailHogPart): string[] {
  return [decodeMailPart(part), ...(part.MIME?.Parts ?? []).flatMap(collectMailText)];
}

/** Returns the verification token from the newest mail sent to `email`, or null until it arrives. */
async function findVerificationToken(mailHogUrl: string, email: string): Promise<string | null> {
  const mailApi = await apiRequest.newContext();
  try {
    const response = await mailApi.get(`${mailHogUrl}/api/v2/search`, { params: { kind: 'to', query: email } });
    if (!response.ok()) {
      throw new Error(`[ingredients] MailHog search returned ${response.status()} ${response.statusText()}.`);
    }
    for (const item of ((await response.json()) as MailHogSearchBody).items ?? []) {
      const parts = [...(item.Content ? [item.Content] : []), ...(item.MIME?.Parts ?? [])];
      for (const text of parts.flatMap(collectMailText)) {
        const match = /verify-email\?token=([^"'&\s<>]+)/.exec(text);
        if (match?.[1]) {
          return new URLSearchParams(`t=${match[1]}`).get('t');
        }
      }
    }
    return null;
  } finally {
    await mailApi.dispose();
  }
}

/**
 * Logs in as qa.nutri via a bare API context (same one-off pattern as
 * `nutritionist/inbox-cooperation-events.spec.ts`'s `loginAsClient2`) to
 * create/read a food directly, bypassing the drawer, so the fixture's
 * per-attempt browser session (a separate, unrelated refresh token — see
 * `../fixtures/auth.ts`'s header comment) is never touched by this token.
 */
async function loginAsNutritionist(baseURL: string): Promise<string> {
  const password = process.env['QA_SEED_PASSWORD'];
  if (!password) {
    throw new Error('[ingredients] QA_SEED_PASSWORD is not set. Copy .env.test.example to .env.test and fill it in.');
  }
  const api = await apiRequest.newContext({ baseURL });
  try {
    const response = await api.post('/auth/login', { data: { email: NUTRITIONIST_EMAIL, password } });
    if (!response.ok()) {
      throw new Error(`[ingredients] login as qa.nutri returned ${response.status()} ${response.statusText()}.`);
    }
    return ((await response.json()) as LoginResponseBody).accessToken;
  } finally {
    await api.dispose();
  }
}

/** Reads the stored visibility of the food with this exact name, via search then GET by id. */
async function fetchFoodVisibilityByName(baseURL: string, name: string): Promise<string | undefined> {
  const accessToken = await loginAsNutritionist(baseURL);
  const api = await apiRequest.newContext({ baseURL });
  const headers = { Authorization: `Bearer ${accessToken}` };
  try {
    const search = await api.get(`/foods/search?q=${encodeURIComponent(name)}`, { headers });
    const { foods } = (await search.json()) as SearchFoodsApiBody;
    const foodId = (foods ?? []).find((candidate) => candidate.name === name || candidate.rawName === name)?.foodId;
    if (!foodId) {
      throw new Error(`[ingredients] GET /foods/search?q=${name} returned no matching food.`);
    }
    const detail = await api.get(`/foods/${foodId}`, { headers });
    return ((await detail.json()) as FoodApiBody).visibility;
  } finally {
    await api.dispose();
  }
}

/** Reads a food's stored main picture URL through the API, as qa.nutri. */
async function fetchFoodImageUrl(baseURL: string, foodId: string): Promise<string | null> {
  const accessToken = await loginAsNutritionist(baseURL);
  const api = await apiRequest.newContext({ baseURL });
  try {
    const response = await api.get(`/foods/${foodId}`, { headers: { Authorization: `Bearer ${accessToken}` } });
    if (!response.ok()) {
      throw new Error(`[ingredients] GET /foods/${foodId} returned ${response.status()} ${response.statusText()}.`);
    }
    return ((await response.json()) as { imageUrl?: string | null }).imageUrl ?? null;
  } finally {
    await api.dispose();
  }
}

/** Reads a food's stored main and extra picture URLs through the API, as qa.nutri. */
async function fetchFoodPictures(
  baseURL: string,
  foodId: string,
): Promise<{ imageUrl: string | null; galleryImageUrls: string[] }> {
  const accessToken = await loginAsNutritionist(baseURL);
  const api = await apiRequest.newContext({ baseURL });
  try {
    const response = await api.get(`/foods/${foodId}`, { headers: { Authorization: `Bearer ${accessToken}` } });
    if (!response.ok()) {
      throw new Error(`[ingredients] GET /foods/${foodId} returned ${response.status()} ${response.statusText()}.`);
    }
    const body = (await response.json()) as { imageUrl?: string | null; galleryImageUrls?: string[] | null };
    return { imageUrl: body.imageUrl ?? null, galleryImageUrls: body.galleryImageUrls ?? [] };
  } finally {
    await api.dispose();
  }
}

/** Finds the caller's own food by its exact name, or `undefined` when there is none. */
async function findOwnFoodIdByName(baseURL: string, name: string): Promise<string | undefined> {
  const accessToken = await loginAsNutritionist(baseURL);
  const api = await apiRequest.newContext({ baseURL });
  try {
    const search = await api.get(`/foods/search?q=${encodeURIComponent(name)}`, {
      headers: { Authorization: `Bearer ${accessToken}` },
    });
    const { foods } = (await search.json()) as SearchFoodsApiBody;
    return (foods ?? []).find((candidate) => candidate.name === name || candidate.rawName === name)?.foodId;
  } finally {
    await api.dispose();
  }
}

/** Creates a private food through the API and returns its id. */
async function createFoodViaApi(baseURL: string, name: string): Promise<string> {
  const accessToken = await loginAsNutritionist(baseURL);
  const api = await apiRequest.newContext({ baseURL });
  try {
    const response = await api.post('/foods', {
      data: {
        name,
        category: 'Fruit',
        nutrientValue: { kcal: 50, protein: 1, carbs: 12, fat: 0 },
        allergens: [],
        dietaryPreferences: [],
        commonServings: [{ label: 'piece', weightGrams: 120 }],
      },
      headers: { Authorization: `Bearer ${accessToken}` },
    });
    const created = (await response.json()) as FoodApiBody;
    if (!response.ok() || !created.foodId) {
      throw new Error(`[ingredients] POST /foods returned ${response.status()} ${response.statusText()}.`);
    }
    return created.foodId;
  } finally {
    await api.dispose();
  }
}

async function deleteFoodViaApi(baseURL: string, foodId: string): Promise<void> {
  const accessToken = await loginAsNutritionist(baseURL);
  const api = await apiRequest.newContext({ baseURL });
  try {
    await api.delete(`/foods/${foodId}`, { headers: { Authorization: `Bearer ${accessToken}` } });
  } finally {
    await api.dispose();
  }
}

/**
 * Deletes every food tag the caller owns via GET + DELETE
 * /trainer/food-tags, so each food-tags test starts and ends from zero
 * tags — CreateFoodTagEndpoint 409s on a duplicate name otherwise. Mirrors
 * `trainer/clients.spec.ts`'s `deleteAllClientTags`.
 */
async function deleteAllFoodTags(baseURL: string | undefined): Promise<void> {
  const resolvedBaseUrl = baseURL ?? 'http://localhost:5173';
  const accessToken = await loginAsNutritionist(resolvedBaseUrl);
  const api = await apiRequest.newContext({ baseURL: resolvedBaseUrl });
  try {
    const listResponse = await api.get('/trainer/food-tags', {
      headers: { Authorization: `Bearer ${accessToken}` },
    });
    if (!listResponse.ok()) {
      throw new Error(`[ingredients] GET /trainer/food-tags returned ${listResponse.status()} ${listResponse.statusText()}.`);
    }
    const { tags } = (await listResponse.json()) as GetFoodTagsApiBody;
    for (const tag of tags ?? []) {
      if (!tag.tagId) {
        continue;
      }
      const deleteResponse = await api.delete(`/trainer/food-tags/${tag.tagId}`, {
        headers: { Authorization: `Bearer ${accessToken}` },
      });
      expect(deleteResponse.ok()).toBe(true);
    }
  } finally {
    await api.dispose();
  }
}

/**
 * Finds a system food's id + name via GET /foods/search — used both to open
 * the read-only drawer for the "assign a tag to a food I don't own" AC, and
 * to assign a tag to it directly via PUT /trainer/foods/{FoodId}/tags. Prefers
 * "Apple" (the same seeded system food `a system row opens the drawer
 * read-only` already relies on).
 */
async function findSystemFood(baseURL: string | undefined): Promise<{ foodId: string; name: string }> {
  const resolvedBaseUrl = baseURL ?? 'http://localhost:5173';
  const accessToken = await loginAsNutritionist(resolvedBaseUrl);
  const api = await apiRequest.newContext({ baseURL: resolvedBaseUrl });
  try {
    const response = await api.get('/foods/search?q=Apple', {
      headers: { Authorization: `Bearer ${accessToken}` },
    });
    if (!response.ok()) {
      throw new Error(`[ingredients] GET /foods/search returned ${response.status()} ${response.statusText()}.`);
    }
    const { foods } = (await response.json()) as SearchFoodsApiBody;
    const systemFood = (foods ?? []).find((food) => food.isSystem && food.name && food.foodId);
    if (!systemFood?.name || !systemFood.foodId) {
      throw new Error('[ingredients] GET /foods/search?q=Apple returned no system food to tag.');
    }
    return { foodId: systemFood.foodId, name: systemFood.name };
  } finally {
    await api.dispose();
  }
}

/** Thin wrapper over `findSystemFood` for callers that only need the name. */
async function findSystemFoodName(baseURL: string | undefined): Promise<string> {
  return (await findSystemFood(baseURL)).name;
}

test.describe('ingredients page', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/ingredients');
    await page.waitForLoadState('networkidle');
    await expect(page.getByRole('heading', { name: 'Ingredients' })).toBeVisible();
  });

  test('list loads with a table and a pagination footer', async ({ page }) => {
    await expect(page.getByText(/Viewing \d+ of \d+/)).toBeVisible();
    await expect(page.getByRole('columnheader', { name: 'Name' })).toBeVisible();
    await expect(page.getByRole('columnheader', { name: 'Library' })).toBeVisible();
  });

  test('searching filters the list by name and updates the URL', async ({ page }) => {
    // Wait for the actual filtered search response (not just the URL/debounce
    // settling) before reading rows — otherwise rows.count() can be read
    // mid-shrink, while the list still holds unfiltered rows that vanish a
    // moment later as the filtered response renders, and a fixed nth(i) index
    // starts pointing at a row that no longer exists.
    const searchResponse = page.waitForResponse(
      (response) => response.url().includes('/foods/search') && response.url().includes('q=Apple'),
    );
    await page.getByPlaceholder('Search ingredients…').fill('Apple');
    await searchResponse;
    await page.waitForURL(/q=Apple/);

    const rows = page.locator('tbody tr');
    await expect(rows.first()).toBeVisible();
    await expect
      .poll(async () => (await rows.allTextContents()).every((text) => /Apple/i.test(text)))
      .toBe(true);
  });

  test('search, filter pills, and the New Ingredient button share one row', async ({ page }) => {
    // #1115 — all four controls sit on a single flex row (IngredientsPage.tsx).
    // Compare vertical centers rather than raw `y`, since the search Input
    // (h-8) and the "+ New Ingredient" Button (size="lg", h-9) have different
    // heights but are vertically centered together by the row's `items-center`.
    const boxes = await Promise.all(
      [
        page.getByPlaceholder('Search ingredients…'),
        page.getByRole('button', { name: /^Category\b/ }),
        page.getByRole('button', { name: 'Tags' }),
        page.getByRole('button', { name: 'New Ingredient' }),
      ].map((locator) => locator.boundingBox()),
    );

    for (const box of boxes) {
      expect(box).not.toBeNull();
    }

    const verticalCenters = boxes.map((box) => box!.y + box!.height / 2);
    const spread = Math.max(...verticalCenters) - Math.min(...verticalCenters);
    expect(spread).toBeLessThan(10);
  });

  test('filtering by two categories returns only rows in those categories', async ({ page }) => {
    await page.getByRole('button', { name: /^Category\b/ }).click();

    const filterResponse = page.waitForResponse(
      (response) => response.url().includes('/foods/search') && response.url().includes('category='),
    );
    await page.getByRole('checkbox', { name: 'Fruit' }).click();
    await page.getByRole('checkbox', { name: 'Dairy' }).click();
    await filterResponse;
    await page.waitForURL(/category=/);
    await page.keyboard.press('Escape');
    await page.waitForLoadState('networkidle');

    const rows = page.locator('tbody tr');
    await expect(rows.first()).toBeVisible();

    // Category is the 4th column (Name, Calories, Nutrients, Category, Library).
    const categoryCells = rows.locator('td:nth-child(4)');
    const categoryTexts = await categoryCells.allTextContents();
    expect(categoryTexts.length).toBeGreaterThan(0);
    for (const text of categoryTexts) {
      expect(['Fruit', 'Dairy']).toContain(text.trim());
    }
  });

  test('clicking the Name column header cycles sort: ascending → descending → cleared, and reorders the list (#1139)', async ({
    page,
  }) => {
    const rows = page.locator('tbody tr');
    await expect(rows.first()).toBeVisible();

    // Narrow to a single category so the whole result set fits on one page —
    // an exact row-order reversal below only holds if sorting isn't also
    // shuffling which page of results comes back.
    await page.getByRole('button', { name: /^Category\b/ }).click();
    const categoryFilterResponse = page.waitForResponse(
      (response) => response.url().includes('/foods/search') && response.url().includes('category='),
    );
    await page.getByRole('checkbox', { name: 'Fruit' }).click();
    await categoryFilterResponse;
    await page.waitForURL(/category=/);
    await page.keyboard.press('Escape');
    await page.waitForLoadState('networkidle');

    const viewingText = await page.getByText(/Viewing \d+ of \d+/).textContent();
    const counts = viewingText?.match(/Viewing (\d+) of (\d+)/);
    expect(counts).not.toBeNull();
    expect(Number(counts![1])).toBe(Number(counts![2]));

    const nameHeader = page.getByRole('columnheader', { name: 'Name' });
    // No `sort` param on first load — the page's default view is Name
    // ascending without the URL saying so explicitly.
    await expect(nameHeader).toHaveAttribute('aria-sort', 'ascending');

    const ascendingNames = (await rows.locator('td:nth-child(1)').allTextContents()).map((text) => text.trim());

    const descendingResponse = page.waitForResponse(
      (response) => response.url().includes('/foods/search') && response.url().includes('sortDir=Descending'),
    );
    await page.getByRole('button', { name: 'Sort by Name' }).click();
    await descendingResponse;
    await page.waitForURL(/sort=name-desc/);
    await expect(nameHeader).toHaveAttribute('aria-sort', 'descending');

    const descendingNames = (await rows.locator('td:nth-child(1)').allTextContents()).map((text) => text.trim());
    expect(descendingNames).toEqual([...ascendingNames].reverse());

    // Third click clears the sort entirely — a distinct URL state
    // (`sort=none`), not a fallback to the default Name-ascending view. The
    // cleared state still requests a deterministic order from the backend —
    // newest first — rather than no sortBy at all (which the backend now
    // treats as Name ascending, the same as the first-load default).
    const clearedResponse = page.waitForResponse(
      (response) =>
        response.url().includes('/foods/search') &&
        response.url().includes('sortBy=DateCreated') &&
        response.url().includes('sortDir=Descending'),
    );
    await page.getByRole('button', { name: 'Sort by Name' }).click();
    await clearedResponse;
    await page.waitForURL(/sort=none/);
    await expect(nameHeader).toHaveAttribute('aria-sort', 'none');

    // The cleared state survives a reload instead of reverting to Name ascending.
    await page.reload();
    await page.waitForLoadState('networkidle');
    await expect(page).toHaveURL(/sort=none/);
    await expect(nameHeader).toHaveAttribute('aria-sort', 'none');
  });

  test('filtering by Owner: Mine returns only ingredients owned by the caller (#1139)', async ({ page }) => {
    const uniqueSuffix = Date.now();
    const name = `QA Owner Filter ${uniqueSuffix}`;

    // Create a private ingredient first so "Mine" has a deterministic match.
    await page.getByRole('button', { name: 'New Ingredient' }).click();
    await expect(page.getByRole('heading', { name: 'New Ingredient' })).toBeVisible();
    await page.getByLabel(/^Name\b/).fill(name);
    await page.getByLabel(/^Category\b/).selectOption('Fruit');
    await page.getByLabel('Calories / 100g').fill('50');
    await page.getByLabel('Protein / 100g').fill('1');
    await page.getByLabel('Carbs / 100g').fill('12');
    await page.getByLabel('Fat / 100g').fill('0');
    await page.getByRole('combobox', { name: /^Unit\b/ }).selectOption('piece');
    await page.getByLabel('Unit weight (g)').fill('120');
    await page.getByRole('button', { name: 'Save Ingredient' }).click();
    await expect(page.getByRole('heading', { name: 'New Ingredient' })).toHaveCount(0);

    await page.getByRole('button', { name: 'Owner' }).click();
    const ownerFilterResponse = page.waitForResponse(
      (response) => response.url().includes('/foods/search') && response.url().includes('owner=Mine'),
    );
    await page.getByRole('checkbox', { name: 'Mine' }).click();
    await ownerFilterResponse;
    await page.waitForURL(/owner=Mine/);
    await page.keyboard.press('Escape');
    await page.waitForLoadState('networkidle');

    const rows = page.locator('tbody tr');
    await expect(rows.first()).toBeVisible();
    // Library is the 6th column for a nutritionist (Name, Calories,
    // Nutrients, Category, Tags, Library).
    const libraryTexts = await rows.locator('td:nth-child(6)').allTextContents();
    expect(libraryTexts.length).toBeGreaterThan(0);
    for (const text of libraryTexts) {
      expect(text.trim()).toBe('Mine');
    }
    await expect(page.getByRole('cell', { name })).toBeVisible();

    // Clean up the ingredient this test created.
    await page.getByRole('cell', { name }).click();
    await page.getByRole('button', { name: 'Delete' }).click();
    await expect(page.getByRole('heading', { name: 'Delete ingredient' })).toBeVisible();
    await page.getByRole('button', { name: 'Delete', exact: true }).last().click();
    await page.waitForLoadState('networkidle');
    await expect(page.getByRole('cell', { name })).toHaveCount(0);
  });

  test("the table header's position doesn't move after scrolling the table body", async ({ page }) => {
    await page.locator('tbody tr').first().waitFor();

    const header = page.getByRole('columnheader', { name: 'Name' });
    const beforeBox = await header.boundingBox();
    expect(beforeBox).not.toBeNull();

    // The page's own vertical scroller -- see IngredientsPage.tsx's
    // `overflow-y-auto` wrapper -- not the window, since that's the ancestor
    // the sticky header actually sticks to.
    const scroller = page.locator('div.overflow-y-auto').filter({ has: page.locator('table') });
    const scrolledTop = await scroller.evaluate((element) => {
      element.scrollTop = 300;
      return element.scrollTop;
    });
    expect(scrolledTop).toBeGreaterThan(0);

    const afterBox = await header.boundingBox();
    expect(afterBox).not.toBeNull();
    expect(afterBox!.y).toBeCloseTo(beforeBox!.y, 0);
  });

  test('a clickable button reports cursor: pointer', async ({ page }) => {
    const cursor = await page
      .getByRole('button', { name: 'New Ingredient' })
      .evaluate((element) => getComputedStyle(element).cursor);
    expect(cursor).toBe('pointer');
  });

  test('a system row opens the drawer read-only', async ({ page }) => {
    await page.getByPlaceholder('Search ingredients…').fill('Apple');
    await page.waitForURL(/q=Apple/);
    await page.waitForLoadState('networkidle');

    const firstRow = page.locator('tbody tr').first();
    await expect(firstRow.getByText('System', { exact: true })).toBeVisible();

    await firstRow.click();
    await expect(page.getByRole('heading', { name: 'Ingredient' })).toBeVisible();
    await expect(page.getByLabel(/^Name\b/)).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Save Ingredient' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Delete' })).toHaveCount(0);
    // Scoped to the sheet footer: the corner "x" close button also has the
    // accessible name "Close" (its sr-only label reuses common.close), so an
    // unscoped getByRole('button', { name: 'Close' }) matches both.
    await page.locator('[data-slot="sheet-footer"]').getByRole('button', { name: 'Close' }).click();
  });

  test('creating, editing, and deleting a private ingredient', async ({ page }) => {
    const uniqueSuffix = Date.now();
    const name = `QA E2E Ingredient ${uniqueSuffix}`;

    await page.getByRole('button', { name: 'New Ingredient' }).click();
    await expect(page.getByRole('heading', { name: 'New Ingredient' })).toBeVisible();

    await page.getByLabel(/^Name\b/).fill(name);
    await page.getByLabel(/^Category\b/).selectOption('Fruit');
    await page.getByLabel('Calories / 100g').fill('50');
    await page.getByLabel('Protein / 100g').fill('1');
    await page.getByLabel('Carbs / 100g').fill('12');
    await page.getByLabel('Fat / 100g').fill('0');
    await page.getByRole('combobox', { name: /^Unit\b/ }).selectOption('piece');
    await page.getByLabel('Unit weight (g)').fill('120');

    await page.getByRole('button', { name: 'Save Ingredient' }).click();
    await expect(page.getByRole('heading', { name: 'New Ingredient' })).toHaveCount(0);

    // Find the freshly-created row via search — tags are no longer a
    // free-text field on the ingredient itself (#1120); see the dedicated
    // "food tags" describe block below for tag create/filter/assign coverage.
    const searchResponse = page.waitForResponse((response) => response.url().includes('/foods/search'));
    await page.getByPlaceholder('Search ingredients…').fill(name);
    await searchResponse;
    await page.waitForLoadState('networkidle');
    await expect(page.getByRole('cell', { name })).toBeVisible();
    await expect(page.getByText('Mine', { exact: true })).toBeVisible();

    // Edit it.
    await page.getByRole('cell', { name }).click();
    await expect(page.getByRole('heading', { name: 'Edit Ingredient' })).toBeVisible();
    await page.getByLabel('Calories / 100g').fill('55');
    await page.getByRole('button', { name: 'Save Ingredient' }).click();
    await expect(page.getByRole('heading', { name: 'Edit Ingredient' })).toHaveCount(0);
    await expect(page.getByText('55 kcal / 100g')).toBeVisible();

    // Delete it, with confirmation.
    await page.getByRole('cell', { name }).click();
    await page.getByRole('button', { name: 'Delete' }).click();
    await expect(page.getByRole('heading', { name: 'Delete ingredient' })).toBeVisible();
    await page.getByRole('button', { name: 'Delete', exact: true }).last().click();
    await expect(page.getByRole('heading', { name: 'Edit Ingredient' })).toHaveCount(0);
    await page.waitForLoadState('networkidle');
    await expect(page.getByRole('cell', { name })).toHaveCount(0);
  });

  test('visibility toggle defaults to Private and round-trips to Public and back', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const name = `QA E2E Visibility ${Date.now()}`;
    const drawer = page.locator('[data-slot="sheet-content"]');

    async function saveAndAssert(expected: 'Public' | 'Private') {
      await page.getByRole('button', { name: 'Save Ingredient' }).click();
      await expect(page.getByRole('heading', { name: 'Edit Ingredient' })).toHaveCount(0);
      expect(await fetchFoodVisibilityByName(origin, name)).toBe(expected);
    }

    await page.getByRole('button', { name: 'New Ingredient' }).click();
    await expect(page.getByRole('heading', { name: 'New Ingredient' })).toBeVisible();
    await expect(drawer.getByRole('radio', { name: 'Private' })).toBeChecked();
    await expect(drawer.getByText('Only you can see and use this ingredient.')).toBeVisible();

    await page.getByLabel(/^Name\b/).fill(name);
    await page.getByLabel(/^Category\b/).selectOption('Fruit');
    await page.getByLabel('Calories / 100g').fill('50');
    await page.getByLabel('Protein / 100g').fill('1');
    await page.getByLabel('Carbs / 100g').fill('12');
    await page.getByLabel('Fat / 100g').fill('0');
    await page.getByRole('combobox', { name: /^Unit\b/ }).selectOption('piece');
    await page.getByLabel('Unit weight (g)').fill('120');
    await page.getByRole('button', { name: 'Save Ingredient' }).click();
    await expect(page.getByRole('heading', { name: 'New Ingredient' })).toHaveCount(0);
    expect(await fetchFoodVisibilityByName(origin, name)).toBe('Private');

    const searchResponse = page.waitForResponse((response) => response.url().includes('/foods/search'));
    await page.getByPlaceholder('Search ingredients…').fill(name);
    await searchResponse;
    await page.waitForLoadState('networkidle');
    await page.getByRole('cell', { name }).click();
    await expect(page.getByRole('heading', { name: 'Edit Ingredient' })).toBeVisible();
    await expect(drawer.getByRole('radio', { name: 'Private' })).toBeChecked();

    await drawer.getByRole('radio', { name: 'Public' }).click();
    await expect(drawer.getByText('All coaches can see and use this ingredient.')).toBeVisible();
    await saveAndAssert('Public');

    // The owner's badge stays "Mine" whatever the visibility.
    await expect(page.getByText('Mine', { exact: true })).toBeVisible();
    await page.getByRole('cell', { name }).click();
    await expect(drawer.getByRole('radio', { name: 'Public' })).toBeChecked();
    await drawer.getByRole('radio', { name: 'Private' }).click();
    await saveAndAssert('Private');

    // Clean up through the UI, with confirmation.
    await page.getByRole('cell', { name }).click();
    await page.getByRole('button', { name: 'Delete' }).click();
    await page.getByRole('button', { name: 'Delete', exact: true }).last().click();
    await expect(page.getByRole('heading', { name: 'Edit Ingredient' })).toHaveCount(0);
  });

  test('empty submit shows translated required errors, never the raw zod message', async ({ page }) => {
    await page.getByRole('button', { name: 'New Ingredient' }).click();
    await expect(page.getByRole('heading', { name: 'New Ingredient' })).toBeVisible();

    // Unit is deliberately not asserted here — it defaults to "Portion" on
    // create, so it's never blank on this path.
    await page.getByRole('button', { name: 'Save Ingredient' }).click();

    await expect(page.getByText('Name is required.')).toBeVisible();
    await expect(page.getByText('Category is required.')).toBeVisible();
    await expect(page.getByText('Calories is required.')).toBeVisible();
    await expect(page.getByText('Protein is required.')).toBeVisible();
    await expect(page.getByText('Carbs is required.')).toBeVisible();
    await expect(page.getByText('Fat is required.')).toBeVisible();
    await expect(page.getByText('Unit weight is required.')).toBeVisible();

    // The zod schema's internal placeholder message is the bare string
    // "required" — before this fix it rendered verbatim for Calories, and
    // Protein/Carbs/Fat rendered no message at all. Assert it never
    // surfaces standalone (a translated sentence like "Calories is
    // required." legitimately contains the word, but never as the whole
    // text of an element).
    await expect(page.getByText('required', { exact: true })).toHaveCount(0);

    await page.getByRole('button', { name: 'Cancel' }).click();
  });

  test('an inconsistent kcal value shows the KCAL_INCONSISTENT inline error on Calories, never a toast', async ({
    page,
  }) => {
    // Scoped to the drawer throughout: the KCAL_INCONSISTENT text used to
    // render twice (inline under Calories AND as a toast, since
    // useCreateFood/useUpdateFood's own onError toasted every error
    // including this one) — an unscoped getByText() would match both and
    // flake depending on whether the toast had already faded. Toast markup
    // lives in `[data-slot="toast-viewport"]`
    // (`@/components/ui/toast.tsx`), never inside the drawer's `dialog`.
    //
    // The "no toast" assertions below pass an explicit short timeout rather
    // than relying on Playwright's default (which happens to equal the
    // toast's own 5s auto-dismiss window, TOAST_DURATION_MS) — the check is
    // for an immediate negative, not "eventually disappears". Both the
    // inline setError() and the (now-skipped) toast call fire synchronously
    // in the same onError callback, so by the time the inline error is
    // visible, a toast — if the bug regressed — would already be mounted;
    // there is nothing to wait for.
    const drawer = page.getByRole('dialog');
    const toastViewport = page.locator('[data-slot="toast-viewport"]');
    const kcalInconsistentText = /doesn.t match macronutrients/i;
    const noToastTimeout = { timeout: 200 };

    // --- Create path ---
    await page.getByRole('button', { name: 'New Ingredient' }).click();
    await expect(page.getByRole('heading', { name: 'New Ingredient' })).toBeVisible();

    const name = `QA Kcal Check ${Date.now()}`;
    await page.getByLabel(/^Name\b/).fill(name);
    await page.getByLabel(/^Category\b/).selectOption('Fruit');
    // 1g protein + 12g carbs + 0g fat = 52 kcal by the ±10% rule; 500 is far
    // outside that range, so CreateFoodEndpoint's KCAL_INCONSISTENT check
    // rejects it with a 400 — the drawer must surface that inline on the
    // Calories field rather than showing nothing (root cause: the NSwag
    // client throws the parsed body directly on 400, not an AxiosError, and
    // getErrorCode only read the AxiosError shape).
    await page.getByLabel('Calories / 100g').fill('500');
    await page.getByLabel('Protein / 100g').fill('1');
    await page.getByLabel('Carbs / 100g').fill('12');
    await page.getByLabel('Fat / 100g').fill('0');
    await page.getByRole('combobox', { name: /^Unit\b/ }).selectOption('piece');
    await page.getByLabel('Unit weight (g)').fill('120');

    await page.getByRole('button', { name: 'Save Ingredient' }).click();

    await expect(drawer.getByText(kcalInconsistentText)).toBeVisible();
    await expect(toastViewport.getByText(kcalInconsistentText)).toHaveCount(0, noToastTimeout);
    // The drawer stays open — the create request was rejected, not fulfilled.
    await expect(page.getByRole('heading', { name: 'New Ingredient' })).toBeVisible();

    // Fix the value and actually create it, so the edit path below has a
    // real, owned row to open.
    await page.getByLabel('Calories / 100g').fill('50');
    await page.getByRole('button', { name: 'Save Ingredient' }).click();
    await expect(page.getByRole('heading', { name: 'New Ingredient' })).toHaveCount(0);

    // --- Edit path ---
    const searchResponse = page.waitForResponse((response) => response.url().includes('/foods/search'));
    await page.getByPlaceholder('Search ingredients…').fill(name);
    await searchResponse;
    await page.waitForLoadState('networkidle');
    await page.getByRole('cell', { name }).click();
    await expect(page.getByRole('heading', { name: 'Edit Ingredient' })).toBeVisible();

    await page.getByLabel('Calories / 100g').fill('500');
    await page.getByRole('button', { name: 'Save Ingredient' }).click();

    await expect(drawer.getByText(kcalInconsistentText)).toBeVisible();
    await expect(toastViewport.getByText(kcalInconsistentText)).toHaveCount(0, noToastTimeout);
    // Still open — the update request was rejected too.
    await expect(page.getByRole('heading', { name: 'Edit Ingredient' })).toBeVisible();

    // Clean up: delete the row this test created.
    await page.getByRole('button', { name: 'Delete' }).click();
    await expect(page.getByRole('heading', { name: 'Delete ingredient' })).toBeVisible();
    await page.getByRole('button', { name: 'Delete', exact: true }).last().click();
    await expect(page.getByRole('heading', { name: 'Edit Ingredient' })).toHaveCount(0);
    await page.waitForLoadState('networkidle');
    await expect(page.getByRole('cell', { name })).toHaveCount(0);
  });

  test('editing without changing Name preserves the base name for a food with per-language display names (rawName)', async ({
    page,
    baseURL,
  }) => {
    // Root cause fixed here: FoodSummary.Name (FoodSummary.cs) is resolved
    // for the request's Accept-Language, while FoodSummary.RawName always
    // mirrors the canonical stored name. valuesFromFood previously loaded
    // `food.name` into the form, so an untouched edit-save round-tripped the
    // TRANSLATED name back into UpdateFoodRequest.name, silently overwriting
    // the food's real base name. This food is created with nameEn distinct
    // from its base name specifically so the UI's `en` Accept-Language
    // resolves `.name` to something the fix must NOT load into the form.
    const origin = baseURL ?? 'http://localhost:5173';
    const uniqueSuffix = Date.now();
    const baseName = `qa-e2e-rawname-${uniqueSuffix}`;
    const nameEn = `${baseName} EN`;

    const createApi = await apiRequest.newContext({ baseURL: origin });
    let foodId: string;
    try {
      const accessToken = await loginAsNutritionist(origin);
      const createResponse = await createApi.post('/foods', {
        data: {
          name: baseName,
          nameEn,
          nameCs: `${baseName} CS`,
          nameDe: `${baseName} DE`,
          category: 'Fruit',
          nutrientValue: { kcal: 50, protein: 1, carbs: 12, fat: 0 },
          allergens: [],
          dietaryPreferences: [],
          commonServings: [{ label: 'piece', weightGrams: 120 }],
        },
        headers: { Authorization: `Bearer ${accessToken}`, 'Accept-Language': 'en' },
      });
      if (!createResponse.ok()) {
        throw new Error(
          `[ingredients] POST /foods (rawName fixture) returned ${createResponse.status()} ` +
            `${createResponse.statusText()}: ${await createResponse.text()}.`,
        );
      }
      const created = (await createResponse.json()) as FoodApiBody;
      if (!created.foodId) {
        throw new Error('[ingredients] POST /foods (rawName fixture) returned no foodId.');
      }
      foodId = created.foodId;
      expect(created.name).toBe(nameEn);
      expect(created.rawName).toBe(baseName);
    } finally {
      await createApi.dispose();
    }

    const searchResponse = page.waitForResponse((response) => response.url().includes('/foods/search'));
    await page.getByPlaceholder('Search ingredients…').fill(nameEn);
    await searchResponse;
    await page.waitForLoadState('networkidle');

    // The row shows the resolved (translated) name — the drawer's Name
    // field must load the CANONICAL name instead.
    await page.getByRole('cell', { name: nameEn }).click();
    await expect(page.getByRole('heading', { name: 'Edit Ingredient' })).toBeVisible();
    await expect(page.getByLabel(/^Name\b/)).toHaveValue(baseName);

    await page.getByRole('button', { name: 'Save Ingredient' }).click();
    await expect(page.getByRole('heading', { name: 'Edit Ingredient' })).toHaveCount(0);

    const verifyApi = await apiRequest.newContext({ baseURL: origin });
    try {
      const accessToken = await loginAsNutritionist(origin);
      const getResponse = await verifyApi.get(`/foods/${foodId}`, {
        headers: { Authorization: `Bearer ${accessToken}`, 'Accept-Language': 'en' },
      });
      const body = (await getResponse.json()) as FoodApiBody;
      expect(body.rawName).toBe(baseName);

      await verifyApi.delete(`/foods/${foodId}`, { headers: { Authorization: `Bearer ${accessToken}` } });
    } finally {
      await verifyApi.dispose();
    }
  });
});

/**
 * Coach-private food tags (#1120): create from the filter popup, assign to
 * a food the caller does NOT own (a system food) through its read-only
 * drawer, and filter by the tag. Nutritionist-only, mirroring
 * `trainer/clients.spec.ts`'s "client tag filter — create a tag (#1119)"
 * describe block.
 */
test.describe('food tags (#1120)', () => {
  test.beforeEach(async ({ page, baseURL }) => {
    await deleteAllFoodTags(baseURL);
    await page.goto('/ingredients');
    await page.waitForLoadState('networkidle');
  });

  test.afterEach(async ({ baseURL }) => {
    await deleteAllFoodTags(baseURL);
  });

  test('creates a tag from the filter popup, assigns it to a system ingredient via the read-only drawer, and filters by it', async ({
    page,
    baseURL,
  }) => {
    const tagName = `QA Food Tag ${Date.now()}`;
    const popoverContent = page.locator("[data-slot='popover-content']");
    const dialog = page.locator("[data-slot='dialog-content']");

    // Create from the filter popover — reopens listed, unticked (mirrors
    // ClientTagFilterPopover / #1119's "+ Create tag" pattern).
    await page.getByRole('button', { name: 'Tags' }).click();
    await expect(popoverContent).toBeVisible();
    await expect(popoverContent.getByText('No tags yet.')).toBeVisible();

    await popoverContent.getByRole('button', { name: 'Create tag' }).click();
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole('heading', { name: 'Create a new tag' })).toBeVisible();
    await dialog.getByLabel(/^Name\b/).fill(tagName);

    const submitButton = dialog.getByRole('button', { name: 'Create tag', exact: true });
    await expect(submitButton).toBeEnabled();
    await submitButton.click();

    await expect(dialog).toBeHidden();
    await expect(popoverContent).toBeVisible();
    const newTagRow = popoverContent.locator('li', { hasText: tagName });
    await expect(newTagRow).toBeVisible();
    await expect(newTagRow.getByRole('checkbox')).not.toBeChecked();
    await page.keyboard.press('Escape');

    // Open a SYSTEM food's read-only drawer and assign the new tag — a
    // nutritionist can tag any visible food, not just their own.
    const systemFoodName = await findSystemFoodName(baseURL);
    const searchResponse = page.waitForResponse((response) => response.url().includes('/foods/search'));
    await page.getByPlaceholder('Search ingredients…').fill(systemFoodName);
    await searchResponse;
    await page.waitForLoadState('networkidle');
    await page.getByRole('cell', { name: systemFoodName, exact: true }).first().click();
    // Scoped to the sheet content for the rest of this drawer-open span: the
    // table row behind the (non-modal) Sheet overlay stays in the DOM, and
    // once the tag is assigned below its own Tags-column chip renders the
    // same tagName text — an unscoped page.getByText(tagName) then resolves
    // two elements and throws a strict-mode violation.
    const drawer = page.locator('[data-slot="sheet-content"]');
    await expect(drawer.getByRole('heading', { name: 'Ingredient' })).toBeVisible();
    await expect(drawer.getByLabel(/^Name\b/)).toBeDisabled();

    await expect(drawer.getByText('My Tags')).toBeVisible();
    await drawer.getByRole('button', { name: 'Assign tags' }).click();
    const drawerPopover = page.locator("[data-slot='popover-content']");
    await expect(drawerPopover).toBeVisible();
    const assignResponse = page.waitForResponse(
      (response) => response.url().includes('/tags') && response.request().method() === 'PUT',
    );
    await drawerPopover.locator('li', { hasText: tagName }).getByRole('checkbox').click();
    await assignResponse;
    await page.keyboard.press('Escape');

    // The tag chip now renders in the drawer — scoped to `drawer`, since the
    // list row underneath (now also showing this tag's chip in its Tags
    // column) still matches the same text (see the comment above).
    await expect(drawer.getByText(tagName, { exact: true })).toBeVisible();

    // Scoped to the sheet footer, same reasoning as the read-only test
    // above: the corner "x" close button shares the accessible name "Close".
    await drawer.locator('[data-slot="sheet-footer"]').getByRole('button', { name: 'Close' }).click();

    // Filter the list by the tag — the system food now matches.
    await page.getByRole('button', { name: 'Tags' }).click();
    await expect(popoverContent).toBeVisible();
    const filterResponse = page.waitForResponse(
      (response) => response.url().includes('/foods/search') && response.url().includes('tagIds='),
    );
    await popoverContent.locator('li', { hasText: tagName }).getByRole('checkbox').click();
    await filterResponse;
    await page.waitForURL(/tags=/);
    await page.waitForLoadState('networkidle');
    await expect(page.getByRole('cell', { name: systemFoodName, exact: true })).toBeVisible();
    await expect(page.getByText('System', { exact: true })).toBeVisible();
  });

  test("a second nutritionist can't see qa.nutri's food tag (#1120)", async ({ baseURL }) => {
    // QA finding: the only prior "a second coach doesn't see it" coverage used
    // a trainer, and a trainer can never own a food tag (Roles(AppRoles.Nutritionist)
    // on every food-tags endpoint — CreateFoodTagEndpoint.cs, GetFoodTagsEndpoint.cs),
    // so it never proved cross-NUTRITIONIST isolation. The seed has only one
    // nutritionist (qa.nutri), so the second nutritionist here is a throwaway
    // account registered fresh through the real API rather than a fixture.
    //
    // API-level only, deliberately: B is driven through the API, which is both
    // sufficient (it's what the UI itself would call) and cheaper than a second
    // browser session.
    //
    // Since #1517 the backend gates unverified accounts centrally: every
    // authenticated endpoint except logout, resend-verification, PUT /auth/email
    // and GET /users/me answers 403 EMAIL_NOT_VERIFIED. A freshly registered B
    // therefore has to verify its email first — the test reads the verification
    // mail from the harness MailHog and posts its token to /auth/verify-email
    // before logging in. The isolation assertions below are unchanged.
    const origin = baseURL ?? 'http://localhost:5173';
    const tagName = `QA Nutri Isolation Tag ${Date.now()}`;

    // --- qa.nutri: create a tag and assign it to a system food, both via the API. ---
    const ownerAccessToken = await loginAsNutritionist(origin);
    const ownerApi = await apiRequest.newContext({ baseURL: origin });
    let systemFood: { foodId: string; name: string };
    let ownerTagId: string;
    try {
      const createResponse = await ownerApi.post('/trainer/food-tags', {
        data: { name: tagName, colorHex: '#3b82f6' },
        headers: { Authorization: `Bearer ${ownerAccessToken}` },
      });
      if (!createResponse.ok()) {
        throw new Error(`[ingredients] POST /trainer/food-tags returned ${createResponse.status()} ${createResponse.statusText()}.`);
      }
      const createdTag = (await createResponse.json()) as FoodTagApiBody;
      if (!createdTag.tagId) {
        throw new Error('[ingredients] POST /trainer/food-tags returned no tagId.');
      }
      ownerTagId = createdTag.tagId;

      systemFood = await findSystemFood(origin);

      const assignResponse = await ownerApi.put(`/trainer/foods/${systemFood.foodId}/tags`, {
        data: { tagIds: [ownerTagId] },
        headers: { Authorization: `Bearer ${ownerAccessToken}` },
      });
      if (!assignResponse.ok()) {
        throw new Error(
          `[ingredients] PUT /trainer/foods/{FoodId}/tags returned ${assignResponse.status()} ${assignResponse.statusText()}.`,
        );
      }
    } finally {
      await ownerApi.dispose();
    }

    // --- A throwaway second nutritionist, registered fresh through the real API. ---
    const secondNutriEmail = `qa.nutri.isolation.${Date.now()}@formup.test`;
    const secondNutriPassword = 'CorrectHorse9';

    const registerApi = await apiRequest.newContext({ baseURL: origin });
    try {
      const registerResponse = await registerApi.post('/auth/register', {
        data: {
          email: secondNutriEmail,
          password: secondNutriPassword,
          confirmPassword: secondNutriPassword,
          firstName: 'QA',
          lastName: 'SecondNutri',
          roles: ['Nutritionist'],
          gdprConsent: true,
        },
      });
      if (!registerResponse.ok()) {
        throw new Error(
          `[ingredients] POST /auth/register (second nutritionist) returned ${registerResponse.status()} ` +
            `${registerResponse.statusText()}: ${await registerResponse.text()}.`,
        );
      }
      // The verification mail is queued, so poll MailHog until it arrives.
      const mailHogUrl = resolveMailHogUrl();
      let verificationToken = '';
      await expect
        .poll(
          async () => {
            verificationToken = (await findVerificationToken(mailHogUrl, secondNutriEmail)) ?? '';
            return verificationToken !== '';
          },
          { message: `verification mail for ${secondNutriEmail} never reached MailHog`, timeout: 15_000 },
        )
        .toBe(true);

      const verifyResponse = await registerApi.post('/auth/verify-email', { data: { token: verificationToken } });
      if (!verifyResponse.ok()) {
        throw new Error(
          `[ingredients] POST /auth/verify-email (second nutritionist) returned ${verifyResponse.status()} ` +
            `${verifyResponse.statusText()}: ${await verifyResponse.text()}.`,
        );
      }
    } finally {
      await registerApi.dispose();
    }

    const secondLoginApi = await apiRequest.newContext({ baseURL: origin });
    let secondNutriAccessToken: string;
    try {
      const loginResponse = await secondLoginApi.post('/auth/login', {
        data: { email: secondNutriEmail, password: secondNutriPassword },
      });
      if (!loginResponse.ok()) {
        throw new Error(
          `[ingredients] POST /auth/login (second nutritionist) returned ${loginResponse.status()} ${loginResponse.statusText()}.`,
        );
      }
      secondNutriAccessToken = ((await loginResponse.json()) as LoginResponseBody).accessToken;
    } finally {
      await secondLoginApi.dispose();
    }

    const secondNutriApi = await apiRequest.newContext({ baseURL: origin });
    try {
      const secondNutriHeaders = { Authorization: `Bearer ${secondNutriAccessToken}` };

      // GET /trainer/food-tags never contains A's tag.
      const tagsResponse = await secondNutriApi.get('/trainer/food-tags', { headers: secondNutriHeaders });
      if (!tagsResponse.ok()) {
        throw new Error(
          `[ingredients] GET /trainer/food-tags (second nutritionist) returned ${tagsResponse.status()} ${tagsResponse.statusText()}.`,
        );
      }
      const { tags } = (await tagsResponse.json()) as GetFoodTagsApiBody;
      expect((tags ?? []).some((tag) => tag.tagId === ownerTagId || tag.name === tagName)).toBe(false);

      // GET the tagged system food never shows A's tag in `tags` — FoodSummary.Tags
      // is looked up scoped to the CALLER's own FoodTagAssignment (FoodTagLookup.cs),
      // so B's own read of the same food returns an empty tag list.
      const getFoodResponse = await secondNutriApi.get(`/foods/${systemFood.foodId}`, { headers: secondNutriHeaders });
      if (!getFoodResponse.ok()) {
        throw new Error(
          `[ingredients] GET /foods/{FoodId} (second nutritionist) returned ${getFoodResponse.status()} ${getFoodResponse.statusText()}.`,
        );
      }
      const foodBody = (await getFoodResponse.json()) as FoodApiBody;
      expect((foodBody.tags ?? []).some((tag) => tag.tagId === ownerTagId || tag.name === tagName)).toBe(false);

      // GET /foods/search?tagIds=<A's tagId> returns zero rows — FindFoodIdsWithAnyTagAsync
      // (FoodTagLookup.cs) scopes the assignment lookup to the CALLER's own OwnerUserId, so
      // A's tagId matches none of B's own assignments and the search short-circuits to an
      // empty page rather than 404/400 (SearchFoodsEndpoint.cs).
      const searchResponse = await secondNutriApi.get('/foods/search', {
        params: { tagIds: ownerTagId },
        headers: secondNutriHeaders,
      });
      if (!searchResponse.ok()) {
        throw new Error(
          `[ingredients] GET /foods/search?tagIds=... (second nutritionist) returned ${searchResponse.status()} ${searchResponse.statusText()}.`,
        );
      }
      const { foods } = (await searchResponse.json()) as SearchFoodsApiBody;
      expect(foods ?? []).toHaveLength(0);

      // PUT /trainer/food-tags/{A's tagId} → 404 — UpdateFoodTagEndpoint scopes its lookup to
      // `ExternalId == req.TagId && OwnerUserId == callerUserId`, so a tag owned by a different
      // nutritionist is indistinguishable from one that doesn't exist at all.
      const updateResponse = await secondNutriApi.put(`/trainer/food-tags/${ownerTagId}`, {
        data: { name: 'B Cannot Rename This', colorHex: '#3b82f6' },
        headers: secondNutriHeaders,
      });
      expect(updateResponse.status()).toBe(404);
    } finally {
      await secondNutriApi.dispose();
    }

    // A's tag itself is cleaned up by this describe block's shared afterEach
    // (deleteAllFoodTags), which already asserts every DELETE response.ok().
  });
});

/**
 * Fibre field and calorie auto-fill (#1126): the drawer's fifth macro
 * field, its create-mode-only Calories auto-fill, and the edit-mode
 * carry-through of sugar/saturatedFat/salt (fields the drawer itself never
 * exposes, but which a full-state PUT must not null out).
 */
test.describe('fibre field and calorie auto-fill (#1126)', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/ingredients');
    await page.waitForLoadState('networkidle');
    await expect(page.getByRole('heading', { name: 'Ingredients' })).toBeVisible();
  });

  test('typing macros auto-fills Calories in create mode', async ({ page }) => {
    await page.getByRole('button', { name: 'New Ingredient' }).click();
    const drawer = page.locator('[data-slot="sheet-content"]');
    await expect(drawer.getByRole('heading', { name: 'New Ingredient' })).toBeVisible();

    await drawer.getByLabel('Protein / 100g').fill('10');
    await drawer.getByLabel('Carbs / 100g').fill('10');
    await drawer.getByLabel('Fat / 100g').fill('0');
    // protein×4 + carbs×4 + fat×9 + fiber×2 = 40 + 40 + 0 + 0 = 80.
    await expect(drawer.getByLabel('Calories / 100g')).toHaveValue('80');

    await drawer.getByLabel('Fiber / 100g').fill('10');
    // Adds fiber×2 = 20 more.
    await expect(drawer.getByLabel('Calories / 100g')).toHaveValue('100');

    await drawer.getByRole('button', { name: 'Cancel' }).click();
  });

  test('a manually entered Calories value survives further macro edits', async ({ page }) => {
    await page.getByRole('button', { name: 'New Ingredient' }).click();
    const drawer = page.locator('[data-slot="sheet-content"]');
    await expect(drawer.getByRole('heading', { name: 'New Ingredient' })).toBeVisible();

    await drawer.getByLabel('Calories / 100g').fill('500');
    // Once the user has typed their own value, a later macro edit must not overwrite it.
    await drawer.getByLabel('Protein / 100g').fill('10');
    await expect(drawer.getByLabel('Calories / 100g')).toHaveValue('500');

    await drawer.getByRole('button', { name: 'Cancel' }).click();
  });

  test('fibre round-trips on save and reload', async ({ page }) => {
    const uniqueSuffix = Date.now();
    const name = `QA Fibre Round Trip ${uniqueSuffix}`;

    await page.getByRole('button', { name: 'New Ingredient' }).click();
    const createDrawer = page.locator('[data-slot="sheet-content"]');
    await expect(createDrawer.getByRole('heading', { name: 'New Ingredient' })).toBeVisible();

    await createDrawer.getByLabel(/^Name\b/).fill(name);
    await createDrawer.getByLabel(/^Category\b/).selectOption('Fruit');
    await createDrawer.getByLabel('Protein / 100g').fill('1');
    await createDrawer.getByLabel('Carbs / 100g').fill('12');
    await createDrawer.getByLabel('Fat / 100g').fill('0');
    await createDrawer.getByLabel('Fiber / 100g').fill('3');
    // 1×4 + 12×4 + 0×9 + 3×2 = 58, auto-filled by the fields above.
    await expect(createDrawer.getByLabel('Calories / 100g')).toHaveValue('58');
    await createDrawer.getByRole('combobox', { name: /^Unit\b/ }).selectOption('piece');
    await createDrawer.getByLabel('Unit weight (g)').fill('120');

    await createDrawer.getByRole('button', { name: 'Save Ingredient' }).click();
    await expect(page.getByRole('heading', { name: 'New Ingredient' })).toHaveCount(0);

    const searchResponse = page.waitForResponse((response) => response.url().includes('/foods/search'));
    await page.getByPlaceholder('Search ingredients…').fill(name);
    await searchResponse;
    await page.waitForLoadState('networkidle');
    await page.getByRole('cell', { name }).click();

    const editDrawer = page.locator('[data-slot="sheet-content"]');
    await expect(editDrawer.getByRole('heading', { name: 'Edit Ingredient' })).toBeVisible();
    await expect(editDrawer.getByLabel('Fiber / 100g')).toHaveValue('3');

    await editDrawer.getByRole('button', { name: 'Delete' }).click();
    await page.getByRole('button', { name: 'Delete', exact: true }).last().click();
    await expect(page.getByRole('heading', { name: 'Edit Ingredient' })).toHaveCount(0);
    await page.waitForLoadState('networkidle');
    await expect(page.getByRole('cell', { name })).toHaveCount(0);
  });

  test('editing an existing food does not auto-fill Calories on a macro change', async ({ page }) => {
    const uniqueSuffix = Date.now();
    const name = `QA Edit No Autofill ${uniqueSuffix}`;

    await page.getByRole('button', { name: 'New Ingredient' }).click();
    const createDrawer = page.locator('[data-slot="sheet-content"]');
    await createDrawer.getByLabel(/^Name\b/).fill(name);
    await createDrawer.getByLabel(/^Category\b/).selectOption('Fruit');
    await createDrawer.getByLabel('Calories / 100g').fill('50');
    await createDrawer.getByLabel('Protein / 100g').fill('1');
    await createDrawer.getByLabel('Carbs / 100g').fill('12');
    await createDrawer.getByLabel('Fat / 100g').fill('0');
    await createDrawer.getByRole('combobox', { name: /^Unit\b/ }).selectOption('piece');
    await createDrawer.getByLabel('Unit weight (g)').fill('120');
    await createDrawer.getByRole('button', { name: 'Save Ingredient' }).click();
    await expect(page.getByRole('heading', { name: 'New Ingredient' })).toHaveCount(0);

    const searchResponse = page.waitForResponse((response) => response.url().includes('/foods/search'));
    await page.getByPlaceholder('Search ingredients…').fill(name);
    await searchResponse;
    await page.waitForLoadState('networkidle');
    await page.getByRole('cell', { name }).click();

    const editDrawer = page.locator('[data-slot="sheet-content"]');
    await expect(editDrawer.getByRole('heading', { name: 'Edit Ingredient' })).toBeVisible();
    await expect(editDrawer.getByLabel('Calories / 100g')).toHaveValue('50');

    // The stored Calories value must win in edit mode — no recompute on a macro edit.
    await editDrawer.getByLabel('Protein / 100g').fill('5');
    await expect(editDrawer.getByLabel('Calories / 100g')).toHaveValue('50');

    await editDrawer.getByRole('button', { name: 'Delete' }).click();
    await page.getByRole('button', { name: 'Delete', exact: true }).last().click();
    await expect(page.getByRole('heading', { name: 'Edit Ingredient' })).toHaveCount(0);
    await page.waitForLoadState('networkidle');
    await expect(page.getByRole('cell', { name })).toHaveCount(0);
  });

  test('editing a food via the drawer preserves its sugar, saturated fat, and salt', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const uniqueSuffix = Date.now();
    const name = `QA Nutrient Carry Through ${uniqueSuffix}`;

    const createApi = await apiRequest.newContext({ baseURL: origin });
    let foodId: string;
    try {
      const accessToken = await loginAsNutritionist(origin);
      const createResponse = await createApi.post('/foods', {
        data: {
          name,
          category: 'Fruit',
          nutrientValue: { kcal: 50, protein: 1, carbs: 12, fat: 0, sugar: 8, saturatedFat: 0.1, salt: 0.01 },
          allergens: [],
          dietaryPreferences: [],
          commonServings: [{ label: 'piece', weightGrams: 120 }],
        },
        headers: { Authorization: `Bearer ${accessToken}` },
      });
      if (!createResponse.ok()) {
        throw new Error(
          `[ingredients] POST /foods (nutrient carry-through fixture) returned ${createResponse.status()} ` +
            `${createResponse.statusText()}: ${await createResponse.text()}.`,
        );
      }
      const created = (await createResponse.json()) as FoodApiBody;
      if (!created.foodId) {
        throw new Error('[ingredients] POST /foods (nutrient carry-through fixture) returned no foodId.');
      }
      foodId = created.foodId;
    } finally {
      await createApi.dispose();
    }

    const searchResponse = page.waitForResponse((response) => response.url().includes('/foods/search'));
    await page.getByPlaceholder('Search ingredients…').fill(name);
    await searchResponse;
    await page.waitForLoadState('networkidle');
    await page.getByRole('cell', { name }).click();

    const editDrawer = page.locator('[data-slot="sheet-content"]');
    await expect(editDrawer.getByRole('heading', { name: 'Edit Ingredient' })).toBeVisible();
    // The only change the drawer itself can make here — sugar/saturatedFat/salt aren't drawer fields.
    // 1×4 + 12×4 + 0×9 + 1×2 = 54; 50/54 ≈ 0.926 stays inside the ±10% KCAL_INCONSISTENT band.
    await editDrawer.getByLabel('Fiber / 100g').fill('1');
    await editDrawer.getByRole('button', { name: 'Save Ingredient' }).click();
    await expect(page.getByRole('heading', { name: 'Edit Ingredient' })).toHaveCount(0);

    const verifyApi = await apiRequest.newContext({ baseURL: origin });
    try {
      const accessToken = await loginAsNutritionist(origin);
      const getResponse = await verifyApi.get(`/foods/${foodId}`, {
        headers: { Authorization: `Bearer ${accessToken}` },
      });
      const body = (await getResponse.json()) as FoodApiBody;
      expect(body.nutrientValue?.fiber).toBe(1);
      expect(body.nutrientValue?.sugar).toBe(8);
      expect(body.nutrientValue?.saturatedFat).toBe(0.1);
      expect(body.nutrientValue?.salt).toBe(0.01);

      await verifyApi.delete(`/foods/${foodId}`, { headers: { Authorization: `Bearer ${accessToken}` } });
    } finally {
      await verifyApi.dispose();
    }
  });
});

/**
 * Two-column nutrition layout and translated serving units (#1133): the
 * macro grid rows Protein+Carbs, Fat+Fibre, Calories alone; a seeded system
 * food's unit key renders translated; a legacy free-text unit label (from
 * before UNIT_KEYS existed) still opens and saves unchanged.
 */
test.describe('nutrition layout and serving units (#1133)', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/ingredients');
    await page.waitForLoadState('networkidle');
    await expect(page.getByRole('heading', { name: 'Ingredients' })).toBeVisible();
  });

  test('the nutrition grid rows Protein+Carbs, then Fat+Fibre, then Calories alone', async ({ page }) => {
    await page.getByRole('button', { name: 'New Ingredient' }).click();
    const drawer = page.locator('[data-slot="sheet-content"]');
    await expect(drawer.getByRole('heading', { name: 'New Ingredient' })).toBeVisible();
    // The heading is visible as soon as the sheet mounts, but its own
    // sheet-in-right slide-in animation (300ms, index.css) is still running
    // at that point — measuring boundingBox() here reads a mid-animation
    // position and flakes the row-grouping assertions below.
    await page.waitForTimeout(300);

    const [proteinBox, carbsBox, fatBox, fiberBox, kcalBox] = await Promise.all([
      drawer.getByLabel('Protein / 100g').boundingBox(),
      drawer.getByLabel('Carbs / 100g').boundingBox(),
      drawer.getByLabel('Fat / 100g').boundingBox(),
      drawer.getByLabel('Fiber / 100g').boundingBox(),
      drawer.getByLabel('Calories / 100g').boundingBox(),
    ]);
    for (const box of [proteinBox, carbsBox, fatBox, fiberBox, kcalBox]) {
      expect(box).not.toBeNull();
    }

    // Same row -> vertical centers within a few px of each other.
    const proteinCenter = proteinBox!.y + proteinBox!.height / 2;
    const carbsCenter = carbsBox!.y + carbsBox!.height / 2;
    const fatCenter = fatBox!.y + fatBox!.height / 2;
    const fiberCenter = fiberBox!.y + fiberBox!.height / 2;
    expect(Math.abs(proteinCenter - carbsCenter)).toBeLessThan(5);
    expect(Math.abs(fatCenter - fiberCenter)).toBeLessThan(5);
    // Each row sits clearly below the previous one, and Calories doesn't span.
    expect(fatCenter).toBeGreaterThan(proteinCenter + 10);
    expect(kcalBox!.y).toBeGreaterThan(fatBox!.y + 10);
    expect(kcalBox!.x).toBeCloseTo(proteinBox!.x, 0);

    await drawer.getByRole('button', { name: 'Cancel' }).click();
  });

  test('a seeded system food shows its translated unit in the Unit select', async ({ page }) => {
    const searchResponse = page.waitForResponse((response) => response.url().includes('/foods/search'));
    await page.getByPlaceholder('Search ingredients…').fill('Canned Chopped Tomatoes');
    await searchResponse;
    await page.waitForLoadState('networkidle');

    await page.getByRole('cell', { name: 'Canned Chopped Tomatoes', exact: true }).first().click();
    const drawer = page.locator('[data-slot="sheet-content"]');
    await expect(drawer.getByRole('heading', { name: 'Ingredient' })).toBeVisible();

    // Role-scoped (not getByLabel): the select's accessible name is "Unit *"
    // (the Label's required-asterisk span), and a plain substring match would
    // also hit the "Unit weight (g)" number input — but that input's role is
    // spinbutton, not combobox, so scoping by role alone disambiguates.
    const unitSelect = drawer.getByRole('combobox', { name: /^Unit\b/ });
    await expect(unitSelect).toBeDisabled();
    const selectedText = await unitSelect.evaluate(
      (element) => (element as HTMLSelectElement).selectedOptions[0]?.textContent,
    );
    expect(selectedText).toBe('Can');

    await drawer.locator('[data-slot="sheet-footer"]').getByRole('button', { name: 'Close' }).click();
  });

  test('an owned food with a legacy free-text unit label opens and saves unchanged', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const uniqueSuffix = Date.now();
    const name = `QA Legacy Unit ${uniqueSuffix}`;
    const legacyLabel = '1 bar (~60g)';

    const createApi = await apiRequest.newContext({ baseURL: origin });
    let foodId: string;
    try {
      const accessToken = await loginAsNutritionist(origin);
      const createResponse = await createApi.post('/foods', {
        data: {
          name,
          category: 'Fruit',
          nutrientValue: { kcal: 50, protein: 1, carbs: 12, fat: 0 },
          allergens: [],
          dietaryPreferences: [],
          commonServings: [{ label: legacyLabel, weightGrams: 60 }],
        },
        headers: { Authorization: `Bearer ${accessToken}` },
      });
      if (!createResponse.ok()) {
        throw new Error(
          `[ingredients] POST /foods (legacy unit fixture) returned ${createResponse.status()} ` +
            `${createResponse.statusText()}: ${await createResponse.text()}.`,
        );
      }
      const created = (await createResponse.json()) as FoodApiBody;
      if (!created.foodId) {
        throw new Error('[ingredients] POST /foods (legacy unit fixture) returned no foodId.');
      }
      foodId = created.foodId;
    } finally {
      await createApi.dispose();
    }

    const searchResponse = page.waitForResponse((response) => response.url().includes('/foods/search'));
    await page.getByPlaceholder('Search ingredients…').fill(name);
    await searchResponse;
    await page.waitForLoadState('networkidle');
    await page.getByRole('cell', { name }).click();

    const editDrawer = page.locator('[data-slot="sheet-content"]');
    await expect(editDrawer.getByRole('heading', { name: 'Edit Ingredient' })).toBeVisible();
    await expect(editDrawer.getByRole('combobox', { name: /^Unit\b/ })).toHaveValue(legacyLabel);

    await editDrawer.getByRole('button', { name: 'Save Ingredient' }).click();
    await expect(page.getByRole('heading', { name: 'Edit Ingredient' })).toHaveCount(0);

    const verifyApi = await apiRequest.newContext({ baseURL: origin });
    try {
      const accessToken = await loginAsNutritionist(origin);
      const getResponse = await verifyApi.get(`/foods/${foodId}`, {
        headers: { Authorization: `Bearer ${accessToken}` },
      });
      const body = (await getResponse.json()) as FoodApiBody;
      expect(body.commonServings?.[0]?.label).toBe(legacyLabel);

      await verifyApi.delete(`/foods/${foodId}`, { headers: { Authorization: `Bearer ${accessToken}` } });
    } finally {
      await verifyApi.dispose();
    }
  });
});

/**
 * Ingredient picture upload, replace, and remove (#1140). The main picture's
 * blob key is deterministic (foods/{id}.jpg), so a replace keeps the SAME
 * url — `ingredient-picture-a.png` and `ingredient-picture-b.png` are
 * different pixel sizes specifically so the test can assert on the loaded
 * image's `naturalWidth` rather than the (unchanged) src string alone.
 * Upload/replace reuse the existing upload-url + confirm flow; remove hits
 * the new DeleteFoodImage endpoint. Follows the browser presigned-PUT
 * pattern from `trainer/inbox-attachments.spec.ts`.
 */
test.describe('ingredient picture (#1140)', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/ingredients');
  });

  test('upload, replace, and remove the main picture', async ({ page, baseURL }) => {
    test.skip(IN_CONTAINER, 'presigned uploads target the host MinIO port');
    const origin = baseURL ?? 'http://localhost:5173';
    const uniqueSuffix = Date.now();
    const name = `QA Picture ${uniqueSuffix}`;

    const createApi = await apiRequest.newContext({ baseURL: origin });
    let foodId: string;
    try {
      const accessToken = await loginAsNutritionist(origin);
      const createResponse = await createApi.post('/foods', {
        data: {
          name,
          category: 'Fruit',
          nutrientValue: { kcal: 50, protein: 1, carbs: 12, fat: 0 },
          allergens: [],
          dietaryPreferences: [],
          commonServings: [{ label: 'piece', weightGrams: 120 }],
        },
        headers: { Authorization: `Bearer ${accessToken}` },
      });
      if (!createResponse.ok()) {
        throw new Error(
          `[ingredients] POST /foods (picture fixture) returned ${createResponse.status()} ${createResponse.statusText()}.`,
        );
      }
      const created = (await createResponse.json()) as FoodApiBody;
      if (!created.foodId) {
        throw new Error('[ingredients] POST /foods (picture fixture) returned no foodId.');
      }
      foodId = created.foodId;
    } finally {
      await createApi.dispose();
    }

    try {
      const searchResponse = page.waitForResponse((response) => response.url().includes('/foods/search'));
      await page.getByPlaceholder('Search ingredients…').fill(name);
      await searchResponse;
      await page.waitForLoadState('networkidle');
      await page.getByRole('cell', { name }).click();

      const drawer = page.locator('[data-slot="sheet-content"]');
      await expect(drawer.getByRole('heading', { name: 'Edit Ingredient' })).toBeVisible();
      await drawer.getByRole('tab', { name: 'Pictures' }).click();
      await expect(drawer.getByRole('button', { name: 'Upload picture' })).toBeVisible();

      // The main picture's input comes first in the DOM; the gallery has its own test id.
      const fileInput = drawer.locator('input[type="file"]').first();
      const pictureImg = drawer.getByAltText('Ingredient picture');

      // A host browser can't resolve the harness's internal MinIO host, so answer stored-picture
      // reads with the fixture most recently uploaded — the picture then really renders.
      let servedFixture = IMAGE_A_PATH;
      await page.route('http://minio-test:9000/**', (route) =>
        route.fulfill({ path: servedFixture, contentType: 'image/png' }),
      );
      const naturalWidth = (locator: Locator) =>
        locator.evaluate((element) => (element as HTMLImageElement).naturalWidth);

      // Upload — the confirm PUT is the signal the picture is actually saved
      // (the earlier browser-PUT to the pre-signed MinIO url is a different
      // origin entirely, so matching on `/foods/{foodId}/image` + method
      // disambiguates it from the upload-url POST, which shares the same
      // path prefix).
      const confirmResponseA = page.waitForResponse(
        (response) => response.url().includes(`/foods/${foodId}/image`) && response.request().method() === 'PUT',
      );
      await fileInput.setInputFiles(IMAGE_A_PATH);
      expect((await confirmResponseA).status()).toBe(204);
      await expect(drawer.getByRole('button', { name: 'Replace picture' })).toBeVisible();
      await expect(drawer.getByRole('button', { name: 'Remove picture' })).toBeVisible();
      expect(await fetchFoodImageUrl(origin, foodId)).toContain(foodId);
      await expect.poll(() => naturalWidth(pictureImg)).toBe(64);

      // The lightbox is portalled to document.body, outside the drawer's subtree.
      await drawer.getByRole('button', { name: 'View picture' }).click();
      const lightbox = page.locator('[data-slot="dialog-content"]');
      await expect(lightbox).toBeVisible();
      await expect.poll(() => naturalWidth(lightbox.locator('img'))).toBe(64);
      await page.keyboard.press('Escape');
      await expect(lightbox).toHaveCount(0);

      // Replace — same blob key, so the new pixel width proves the cache-buster works.
      servedFixture = IMAGE_B_PATH;
      const confirmResponseB = page.waitForResponse(
        (response) => response.url().includes(`/foods/${foodId}/image`) && response.request().method() === 'PUT',
      );
      await fileInput.setInputFiles(IMAGE_B_PATH);
      expect((await confirmResponseB).status()).toBe(204);
      await expect.poll(() => naturalWidth(pictureImg)).toBe(128);

      // The list thumbnail opens the same lightbox without opening the drawer. The open drawer is
      // modal and hides the table from the accessibility tree, so close it first.
      await drawer.locator('[data-slot="sheet-footer"]').getByRole('button', { name: 'Cancel' }).click();
      await expect(drawer).toHaveCount(0);
      const thumbnailButton = page.getByRole('button', { name: `View picture of ${name}` });
      await expect.poll(() => naturalWidth(thumbnailButton.locator('img'))).toBe(128);
      await thumbnailButton.click();
      await expect(lightbox).toBeVisible();
      await expect(drawer).toHaveCount(0);
      await page.keyboard.press('Escape');
      await expect(lightbox).toHaveCount(0);
      await page.getByRole('cell', { name }).getByText(name, { exact: true }).click();
      await expect(drawer.getByRole('heading', { name: 'Edit Ingredient' })).toBeVisible();
      await drawer.getByRole('tab', { name: 'Pictures' }).click();

      // Remove, with confirmation.
      const removeResponse = page.waitForResponse(
        (response) => response.url().includes(`/foods/${foodId}/image`) && response.request().method() === 'DELETE',
      );
      await drawer.getByRole('button', { name: 'Remove picture' }).click();
      await expect(page.getByRole('heading', { name: 'Remove picture' })).toBeVisible();
      await page.getByRole('button', { name: 'Remove', exact: true }).click();
      expect((await removeResponse).status()).toBe(204);
      await expect(pictureImg).toHaveCount(0);
      await expect(drawer.getByRole('button', { name: 'Upload picture' })).toBeVisible();
      await expect(drawer.getByRole('button', { name: 'Remove picture' })).toHaveCount(0);
      expect(await fetchFoodImageUrl(origin, foodId)).toBeNull();

      await drawer.locator('[data-slot="sheet-footer"]').getByRole('button', { name: 'Cancel' }).click();
    } finally {
      const cleanupApi = await apiRequest.newContext({ baseURL: origin });
      try {
        const accessToken = await loginAsNutritionist(origin);
        await cleanupApi.delete(`/foods/${foodId}`, { headers: { Authorization: `Bearer ${accessToken}` } });
      } finally {
        await cleanupApi.dispose();
      }
    }
  });

  test('dropping a file on the empty zone uploads the picture (#1140)', async ({ page, baseURL }) => {
    test.skip(IN_CONTAINER, 'presigned uploads target the host MinIO port');
    const origin = baseURL ?? 'http://localhost:5173';
    const uniqueSuffix = Date.now();
    const name = `QA Picture Drop ${uniqueSuffix}`;

    const createApi = await apiRequest.newContext({ baseURL: origin });
    let foodId: string;
    try {
      const accessToken = await loginAsNutritionist(origin);
      const createResponse = await createApi.post('/foods', {
        data: {
          name,
          category: 'Fruit',
          nutrientValue: { kcal: 50, protein: 1, carbs: 12, fat: 0 },
          allergens: [],
          dietaryPreferences: [],
          commonServings: [{ label: 'piece', weightGrams: 120 }],
        },
        headers: { Authorization: `Bearer ${accessToken}` },
      });
      if (!createResponse.ok()) {
        throw new Error(
          `[ingredients] POST /foods (picture drop fixture) returned ${createResponse.status()} ${createResponse.statusText()}.`,
        );
      }
      const created = (await createResponse.json()) as FoodApiBody;
      if (!created.foodId) {
        throw new Error('[ingredients] POST /foods (picture drop fixture) returned no foodId.');
      }
      foodId = created.foodId;
    } finally {
      await createApi.dispose();
    }

    try {
      const searchResponse = page.waitForResponse((response) => response.url().includes('/foods/search'));
      await page.getByPlaceholder('Search ingredients…').fill(name);
      await searchResponse;
      await page.waitForLoadState('networkidle');
      await page.getByRole('cell', { name }).click();

      const drawer = page.locator('[data-slot="sheet-content"]');
      await expect(drawer.getByRole('heading', { name: 'Edit Ingredient' })).toBeVisible();
      await drawer.getByRole('tab', { name: 'Pictures' }).click();
      const dropZone = drawer.getByRole('button', { name: 'Upload picture' });
      await expect(dropZone).toBeVisible();

      // Builds a DataTransfer carrying the fixture's actual bytes inside the
      // page, then dispatches the native drag sequence on the drop zone —
      // the documented Playwright recipe for simulating an OS file drop,
      // since a DataTransfer (and the File it carries) can't cross the
      // Node/browser boundary as a plain value.
      const imageBase64 = readFileSync(IMAGE_A_PATH).toString('base64');
      const dataTransfer = await page.evaluateHandle(
        ({ base64, fileName, mimeType }) => {
          const transfer = new DataTransfer();
          const bytes = Uint8Array.from(atob(base64), (char) => char.charCodeAt(0));
          const file = new File([bytes], fileName, { type: mimeType });
          transfer.items.add(file);
          return transfer;
        },
        { base64: imageBase64, fileName: 'ingredient-picture-a.png', mimeType: 'image/png' },
      );

      const confirmResponse = page.waitForResponse(
        (response) => response.url().includes(`/foods/${foodId}/image`) && response.request().method() === 'PUT',
      );
      await dropZone.dispatchEvent('dragenter', { dataTransfer });
      await dropZone.dispatchEvent('dragover', { dataTransfer });
      await dropZone.dispatchEvent('drop', { dataTransfer });
      expect((await confirmResponse).status()).toBe(204);
      await expect(drawer.getByRole('button', { name: 'Replace picture' })).toBeVisible();
      expect(await fetchFoodImageUrl(origin, foodId)).toContain(foodId);

      await drawer.locator('[data-slot="sheet-footer"]').getByRole('button', { name: 'Cancel' }).click();
    } finally {
      const cleanupApi = await apiRequest.newContext({ baseURL: origin });
      try {
        const accessToken = await loginAsNutritionist(origin);
        await cleanupApi.delete(`/foods/${foodId}`, { headers: { Authorization: `Bearer ${accessToken}` } });
      } finally {
        await cleanupApi.dispose();
      }
    }
  });

  test('add an extra picture, set it as main, then remove an extra', async ({ page, baseURL }) => {
    test.skip(IN_CONTAINER, 'presigned uploads target the host MinIO port');
    const origin = baseURL ?? 'http://localhost:5173';
    const name = `QA Picture Gallery ${Date.now()}`;
    const foodId = await createFoodViaApi(origin, name);

    try {
      await page.route('http://minio-test:9000/**', (route) =>
        route.fulfill({ path: IMAGE_A_PATH, contentType: 'image/png' }),
      );

      const searchResponse = page.waitForResponse((response) => response.url().includes('/foods/search'));
      await page.getByPlaceholder('Search ingredients…').fill(name);
      await searchResponse;
      await page.waitForLoadState('networkidle');
      await page.getByRole('cell', { name }).click();
      const drawer = page.locator('[data-slot="sheet-content"]');
      await expect(drawer.getByRole('heading', { name: 'Edit Ingredient' })).toBeVisible();
      await drawer.getByRole('tab', { name: 'Pictures' }).click();

      const isImagePut = (slot: string) => (response: { url(): string; request(): { method(): string } }) =>
        response.url().includes(`/foods/${foodId}/image`) &&
        response.url().includes(`slot=${slot}`) &&
        response.request().method() === 'PUT';

      // Main picture first, so the swap below has a main to hand over.
      const mainConfirm = page.waitForResponse(isImagePut('main'));
      await drawer.locator('input[type="file"]').first().setInputFiles(IMAGE_A_PATH);
      expect((await mainConfirm).status()).toBe(204);
      await expect(drawer.getByRole('button', { name: 'Replace picture' })).toBeVisible();

      // Add an extra picture.
      const galleryConfirm = page.waitForResponse(isImagePut('gallery'));
      await drawer.getByTestId('ingredient-gallery-input').setInputFiles(IMAGE_A_PATH);
      expect((await galleryConfirm).status()).toBe(204);
      await expect(drawer.getByTestId('ingredient-gallery-tile')).toHaveCount(1);
      const afterAdd = await fetchFoodPictures(origin, foodId);
      expect(afterAdd.imageUrl).not.toBeNull();
      expect(afterAdd.galleryImageUrls).toHaveLength(1);

      // Set the extra as main: the old main takes its slot.
      const promoteResponse = page.waitForResponse(
        (response) =>
          response.url().includes(`/foods/${foodId}/gallery/promote`) && response.request().method() === 'POST',
      );
      await drawer.getByRole('button', { name: 'Set picture 1 as main' }).click();
      expect((await promoteResponse).status()).toBe(204);
      await expect
        .poll(async () => (await fetchFoodPictures(origin, foodId)).imageUrl)
        .toBe(afterAdd.galleryImageUrls[0]);
      const afterPromote = await fetchFoodPictures(origin, foodId);
      expect(afterPromote.galleryImageUrls).toEqual([afterAdd.imageUrl]);
      await expect(drawer.getByTestId('ingredient-gallery-tile')).toHaveCount(1);

      // Remove the extra (now the previous main), with confirmation.
      const removeResponse = page.waitForResponse(
        (response) =>
          response.url().includes(`/foods/${foodId}/gallery`) && response.request().method() === 'DELETE',
      );
      await drawer.getByRole('button', { name: 'Remove picture 1' }).click();
      await page.getByRole('button', { name: 'Remove', exact: true }).click();
      expect((await removeResponse).status()).toBe(204);
      await expect(drawer.getByTestId('ingredient-gallery-tile')).toHaveCount(0);
      expect((await fetchFoodPictures(origin, foodId)).galleryImageUrls).toEqual([]);
    } finally {
      await deleteFoodViaApi(origin, foodId);
    }
  });

  test('create with staged pictures uploads them once the ingredient is saved', async ({ page, baseURL }) => {
    test.skip(IN_CONTAINER, 'presigned uploads target the host MinIO port');
    const origin = baseURL ?? 'http://localhost:5173';
    const name = `QA Picture Staged ${Date.now()}`;
    const drawer = page.locator('[data-slot="sheet-content"]');

    try {
      await page.getByRole('button', { name: 'New Ingredient' }).click();
      await expect(drawer.getByRole('heading', { name: 'New Ingredient' })).toBeVisible();
      await drawer.getByLabel(/^Name\b/).fill(name);
      await drawer.getByLabel(/^Category\b/).selectOption('Fruit');
      await drawer.getByLabel('Calories / 100g').fill('50');
      await drawer.getByLabel('Protein / 100g').fill('1');
      await drawer.getByLabel('Carbs / 100g').fill('12');
      await drawer.getByLabel('Fat / 100g').fill('0');
      await drawer.getByRole('combobox', { name: /^Unit\b/ }).selectOption('piece');
      await drawer.getByLabel('Unit weight (g)').fill('120');

      // Stage a main picture and two extras; the form values survive the tab switch.
      await drawer.getByRole('tab', { name: 'Pictures' }).click();
      await drawer.getByTestId('staged-main-input').setInputFiles(IMAGE_A_PATH);
      await expect(drawer.getByRole('button', { name: 'Replace picture' })).toBeVisible();
      await drawer.getByTestId('ingredient-gallery-input').setInputFiles([IMAGE_A_PATH, IMAGE_A_PATH]);
      await expect(drawer.getByTestId('ingredient-gallery-tile')).toHaveCount(2);
      // Nothing is sent to the server before the ingredient is saved.
      expect(await findOwnFoodIdByName(origin, name)).toBeUndefined();

      const imageRequests: string[] = [];
      page.on('request', (request) => {
        if (/\/foods\/[^/]+\/image\/upload-url/.test(request.url()) && request.method() === 'POST') {
          imageRequests.push(new URL(request.url()).searchParams.get('slot') ?? '');
        }
      });
      await drawer.getByRole('button', { name: 'Save Ingredient' }).click();
      await expect(drawer).toHaveCount(0);

      // Main first, then the two extras, sequentially.
      await expect.poll(() => imageRequests).toEqual(['main', 'gallery', 'gallery']);
      const foodId = await findOwnFoodIdByName(origin, name);
      expect(foodId).toBeDefined();
      await expect
        .poll(async () => (await fetchFoodPictures(origin, foodId as string)).galleryImageUrls.length)
        .toBe(2);
      expect((await fetchFoodPictures(origin, foodId as string)).imageUrl).not.toBeNull();
    } finally {
      const foodId = await findOwnFoodIdByName(origin, name);
      if (foodId) {
        await deleteFoodViaApi(origin, foodId);
      }
    }
  });

  test('a food the caller does not own shows its picture read-only, with no controls', async ({ page, baseURL }) => {
    const systemFoodName = await findSystemFoodName(baseURL);
    const searchResponse = page.waitForResponse((response) => response.url().includes('/foods/search'));
    await page.getByPlaceholder('Search ingredients…').fill(systemFoodName);
    await searchResponse;
    await page.waitForLoadState('networkidle');
    await page.getByRole('cell', { name: systemFoodName, exact: true }).first().click();

    const drawer = page.locator('[data-slot="sheet-content"]');
    await expect(drawer.getByRole('heading', { name: 'Ingredient' })).toBeVisible();
    await drawer.getByRole('tab', { name: 'Pictures' }).click();
    await expect(drawer.getByText('Picture', { exact: true })).toBeVisible();
    await expect(drawer.getByRole('button', { name: 'Upload picture' })).toHaveCount(0);
    await expect(drawer.getByRole('button', { name: 'Replace picture' })).toHaveCount(0);
    await expect(drawer.getByRole('button', { name: 'Remove picture' })).toHaveCount(0);

    await drawer.locator('[data-slot="sheet-footer"]').getByRole('button', { name: 'Close' }).click();
  });
});

/**
 * A trainer-only coach has no food tags (only a nutritionist can create one)
 * and, since #1325, no access to the Ingredients page at all: RoleGuard sends
 * them to /clients. Uses `trainerTest` directly (see this file's header
 * comment on why that still runs under the `nutritionist` Playwright project).
 */
trainerTest.describe('food tags — trainer-only coach (#1120)', () => {
  trainerTest('redirects a trainer-only coach to clients', async ({ page }) => {
    await page.goto('/ingredients');
    await expect(page).toHaveURL(/\/clients$/);
  });
});
