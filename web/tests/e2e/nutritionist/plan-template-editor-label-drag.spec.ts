/**
 * Plan editor label drag (epic #1424, tasks #1437 and #1444): dragging a meal row's label onto another row, or a
 * weekday's name onto another day, moves its contents there and shifts what lies between; nothing is created, so
 * a day without the row stays as it is. Holding Alt copies instead: a target without food takes the copy at once,
 * one with food asks Replace or Append. Alt+Arrow on a focused label moves it one step. Each move is one Undo
 * step. Runs against the real harness, no mocked APIs.
 *
 * A meal's contents are told apart by how many ingredients it holds (its cell shows "N items"). Every meal is
 * seeded through the API; nothing is added by clicking the library.
 */
import { request as apiRequest, type Locator, type Page } from '@playwright/test';
import { nutritionistTest as test, expect } from '../fixtures/auth';

const NUTRITIONIST_EMAIL = 'qa.nutri@formup.test';
const DAILY_KCAL = 2000;
const CLIENT_PROFILE_ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const MAX_MEALS_PER_DAY = 20;
const LOG_TAG = '[plan-template-editor-label-drag]';

interface LoginResponseBody {
  accessToken: string;
}

interface CreatedBody {
  templateId?: string;
}

interface SeedMeal {
  kind: string;
  /** How many ingredients the meal holds. */
  count: number;
}

/** The meals a weekday holds (dayIndex 0 is Monday), in order. */
type DaySeed = (dayIndex: number) => SeedMeal[];

type Auth = Record<string, string>;
type Api = Awaited<ReturnType<typeof apiRequest.newContext>>;

async function withApi<T>(baseURL: string, run: (api: Api, auth: Auth) => Promise<T>): Promise<T> {
  const password = process.env['QA_SEED_PASSWORD'];
  if (!password) {
    throw new Error(`${LOG_TAG} QA_SEED_PASSWORD is not set. Copy .env.test.example to .env.test and fill it in.`);
  }
  const api = await apiRequest.newContext({ baseURL });
  try {
    const login = await api.post('/auth/login', { data: { email: NUTRITIONIST_EMAIL, password } });
    if (!login.ok()) {
      throw new Error(`${LOG_TAG} login as qa.nutri returned ${login.status()}.`);
    }
    const { accessToken } = (await login.json()) as LoginResponseBody;
    return await run(api, { Authorization: `Bearer ${accessToken}` });
  } finally {
    await api.dispose();
  }
}

interface Food {
  foodId: string;
  name: string;
  nutrientValue: Record<string, number | null>;
}

interface FoodsSearchBody {
  foods?: { foodId?: string; name?: string; nutrientValue?: Record<string, number | null> }[];
}

/** The ingredient every seeded meal holds: found by its exact name, never by list position. */
async function findApple(api: Api, headers: Auth): Promise<Food> {
  const search = await api.get('/foods/search?q=Apple', { headers });
  const foods = ((await search.json()) as FoodsSearchBody).foods ?? [];
  const found = foods.find((food) => food.name?.toLowerCase() === 'apple' && food.foodId && food.nutrientValue);
  if (!found?.foodId || !found.name || !found.nutrientValue) {
    throw new Error(`${LOG_TAG} GET /foods/search?q=Apple returned no food named exactly "Apple".`);
  }
  return { foodId: found.foodId, name: found.name, nutrientValue: found.nutrientValue };
}

function foodsOf(food: Food, count: number): unknown[] {
  return Array.from({ length: count }, () => ({
    foodExternalId: food.foodId,
    foodName: food.name,
    nutrientValuePer100Grams: food.nutrientValue,
    amountGrams: 100,
  }));
}

function buildWeeks(food: Food, days: DaySeed): unknown[] {
  return [
    {
      weekNumber: 1,
      days: Array.from({ length: 7 }, (_, dayIndex) => ({
        dayOfWeek: dayIndex + 1,
        meals: days(dayIndex).map((meal, mealIndex) => ({
          kind: meal.kind,
          order: mealIndex + 1,
          foods: foodsOf(food, meal.count),
          recipes: [],
        })),
      })),
    },
  ];
}

/** One week built from the seed; the meals come pre-filled so the editor opens ready, with nothing unsaved. */
async function createTemplate(baseURL: string, name: string, days: DaySeed): Promise<string> {
  return withApi(baseURL, async (api, headers) => {
    const food = await findApple(api, headers);
    const response = await api.post('/nutrition/plan-templates', {
      headers,
      data: { name, goal: 'Maintain', globalSettings: { dailyKcal: DAILY_KCAL }, weeks: buildWeeks(food, days) },
    });
    if (!response.ok()) {
      throw new Error(`${LOG_TAG} POST /nutrition/plan-templates returned ${response.status()}: ${await response.text()}`);
    }
    const templateId = ((await response.json()) as CreatedBody).templateId;
    if (!templateId) {
      throw new Error(`${LOG_TAG} create response carried no templateId.`);
    }
    return templateId;
  });
}

async function deleteTemplate(baseURL: string, templateId: string): Promise<void> {
  await withApi(baseURL, (api, headers) => api.delete(`/nutrition/plan-templates/${templateId}`, { headers }));
}

interface SavedMealsBody {
  weeks?: { days?: { meals?: { mealId?: string }[] }[] }[];
}

interface TemplateDetailBody {
  weeks?: { days?: { dayOfWeek: number; meals?: { kind: string }[] }[] }[];
}

/** The meal kinds each weekday of week 1 holds as the server stores them. */
async function savedKinds(baseURL: string, templateId: string): Promise<Record<number, string[]>> {
  return withApi(baseURL, async (api, headers) => {
    const detail = await api.get(`/nutrition/plan-templates/${templateId}`, { headers });
    const body = (await detail.json()) as TemplateDetailBody;
    const result: Record<number, string[]> = {};
    for (const day of body.weeks?.[0]?.days ?? []) {
      result[day.dayOfWeek] = (day.meals ?? []).map((meal) => meal.kind);
    }
    return result;
  });
}

const DAY_MS = 24 * 60 * 60 * 1000;

/**
 * A Monday ten weeks out, as YYYY-MM-DD: publishable (never in the past) and clear of the other specs' plans.
 * Start weeks reserved for this client (specs run in parallel, overlapping plans get 409 PLAN_OVERLAP):
 * +3 client-plan-editor, +10 plan-template-editor-label-drag (this spec), +12 plan-editor-sync-conflicts,
 * +14 plan-template-editor-meal-reorder.
 */
function farFutureMonday(): string {
  const now = new Date();
  const today = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
  const daysToNextMonday = (8 - new Date(today).getUTCDay()) % 7 || 7;
  return new Date(today + (daysToNextMonday + 70) * DAY_MS).toISOString().slice(0, 10);
}

async function readVersion(api: Api, headers: Auth, planId: string): Promise<number | undefined> {
  const detail = await api.get(`/nutrition/plans/${planId}`, { headers });
  return ((await detail.json()) as { version?: number }).version;
}

/**
 * A client plan with one week of the given meals. A draft stays editable; `completed` publishes week 1
 * (POST /nutrition/plans/{PlanId}/weeks/{WeekNumber}/publish with no body, which needs a start date and
 * makes the plan Active), then POST /nutrition/plans/{PlanId}/complete with `{ version }`. The client editor
 * treats a Completed plan as read-only. DELETE permanently removes a plan that has not started, so the spec
 * deletes it afterwards.
 */
async function createClientPlan(
  baseURL: string,
  name: string,
  days: DaySeed,
  finish: 'draft' | 'completed',
): Promise<string> {
  return withApi(baseURL, async (api, headers) => {
    const food = await findApple(api, headers);
    const startDate = farFutureMonday();
    const created = await api.post('/nutrition/plans', {
      headers,
      data: { clientId: CLIENT_PROFILE_ID, name, weekCount: 1, globalSettings: { dailyKcal: DAILY_KCAL }, startDate },
    });
    if (!created.ok()) {
      throw new Error(`${LOG_TAG} POST /nutrition/plans returned ${created.status()}: ${await created.text()}`);
    }
    const planId = ((await created.json()) as { planId?: string }).planId;
    if (!planId) {
      throw new Error(`${LOG_TAG} POST /nutrition/plans returned no planId.`);
    }
    try {
      const update = await api.put(`/nutrition/plans/${planId}`, {
        headers,
        data: {
          name,
          globalSettings: { dailyKcal: DAILY_KCAL },
          version: await readVersion(api, headers, planId),
          startDate,
          supplements: [],
          weeks: buildWeeks(food, days),
        },
      });
      if (!update.ok()) {
        throw new Error(`${LOG_TAG} PUT /nutrition/plans/${planId} returned ${update.status()}: ${await update.text()}`);
      }
      if (finish === 'completed') {
        const published = await api.post(`/nutrition/plans/${planId}/weeks/1/publish`, { headers });
        if (!published.ok()) {
          throw new Error(`${LOG_TAG} publish week 1 returned ${published.status()}: ${await published.text()}`);
        }
        const completed = await api.post(`/nutrition/plans/${planId}/complete`, {
          headers,
          data: { version: await readVersion(api, headers, planId) },
        });
        if (!completed.ok()) {
          throw new Error(`${LOG_TAG} complete returned ${completed.status()}: ${await completed.text()}`);
        }
      }
    } catch (error) {
      await api.delete(`/nutrition/plans/${planId}`, { headers });
      throw error;
    }
    return planId;
  });
}

async function deletePlan(baseURL: string, planId: string): Promise<void> {
  await withApi(baseURL, (api, headers) => api.delete(`/nutrition/plans/${planId}`, { headers }));
}

function cell(page: Page, dayOfWeek: number, rowIndex: number): Locator {
  return page.locator(`[data-testid="meal-cell"][data-day="${dayOfWeek}"][data-row="${rowIndex}"]`);
}

function rowLabel(page: Page, rowIndex: number): Locator {
  return page.locator(`[data-testid="row-label"][data-index="${rowIndex}"]`);
}

function rowFrame(page: Page, rowIndex: number): Locator {
  return page.locator(`[data-testid="row-label-frame"][data-index="${rowIndex}"]`);
}

function dayLabel(page: Page, dayOfWeek: number): Locator {
  return page.locator(`[data-testid="day-label"][data-index="${dayOfWeek}"]`);
}

function dayFrame(page: Page, dayOfWeek: number): Locator {
  return page.locator(`[data-testid="day-label-frame"][data-index="${dayOfWeek}"]`);
}

/** Expects a cell to hold the given number of ingredients, or to be empty. */
async function expectCell(page: Page, dayOfWeek: number, rowIndex: number, count: number | 'Empty'): Promise<void> {
  await expect(cell(page, dayOfWeek, rowIndex)).toContainText(count === 'Empty' ? 'Empty' : `${count} items`);
}

/** Scrolls the element into view, then waits until two reads of its box match. */
async function settledBox(locator: Locator): Promise<{ x: number; y: number; width: number; height: number }> {
  await locator.scrollIntoViewIfNeeded();
  let previous = '';
  let box = await locator.boundingBox();
  for (let attempt = 0; attempt < 20; attempt += 1) {
    const current = JSON.stringify(box);
    if (current === previous && box) {
      return box;
    }
    previous = current;
    await locator.page().waitForTimeout(50);
    box = await locator.boundingBox();
  }
  if (!box) {
    throw new Error(`${LOG_TAG} drag source or target is not visible.`);
  }
  return box;
}

/** Presses on the handle and travels far enough that dnd-kit starts the drag. */
async function beginDrag(page: Page, handle: Locator): Promise<void> {
  const from = await settledBox(handle);
  await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
  await page.mouse.down();
  await page.mouse.move(from.x + from.width / 2 + 12, from.y + from.height / 2 + 12, { steps: 4 });
}

/**
 * Moves into the label's drop zone and waits until it reports itself as the drop target. The zone is measured
 * here, after the drag has started, on every attempt; small moves inside it repeat the pointer events until the
 * library has picked it.
 */
async function settleOnFrame(page: Page, frame: Locator): Promise<void> {
  await expect(async () => {
    const box = await settledBox(frame);
    const x = box.x + box.width / 2;
    const y = box.y + box.height / 2;
    await page.mouse.move(x, y, { steps: 8 });
    await page.mouse.move(x - 2, y, { steps: 2 });
    await page.mouse.move(x, y, { steps: 2 });
    await expect(frame).toHaveAttribute('data-over', 'true', { timeout: 500 });
  }).toPass({ timeout: 5000 });
}

/** Drags a label onto a frame and drops it there; `alt` holds Alt for the whole drag, which copies instead of moving. */
async function dragLabel(page: Page, handle: Locator, frame: Locator, options: { alt?: boolean } = {}): Promise<void> {
  // Scroll both ends into view first, so reading one box cannot move the other.
  await handle.scrollIntoViewIfNeeded();
  await frame.scrollIntoViewIfNeeded();
  if (options.alt) {
    await page.keyboard.down('Alt');
  }
  try {
    await beginDrag(page, handle);
    await settleOnFrame(page, frame);
    await page.mouse.up();
  } finally {
    if (options.alt) {
      await page.keyboard.up('Alt');
    }
  }
}

async function undo(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Undo' }).click();
}

/** A meal list per weekday: the same for every day, or each day's own. */
function same(meals: SeedMeal[]): DaySeed {
  return () => meals;
}

const B = (count: number): SeedMeal => ({ kind: 'Breakfast', count });
const L = (count: number): SeedMeal => ({ kind: 'Lunch', count });
const D = (count: number): SeedMeal => ({ kind: 'Dinner', count });

// Tall enough for the day-name row, Day total and a few meal rows without scrolling: dragging near the scroll
// area's edge would auto-scroll it and move the drop zones away from the pointer.
test.use({ viewport: { width: 1920, height: 1200 } });

test.describe('plan editor label drag', () => {
  test('a row Alt-dropped on an empty row copies it across the week, and one Undo restores it', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    // Lunch is empty on every day; Breakfast holds 2 on Monday and 3 on Tuesday.
    const days: DaySeed = (dayIndex) => [B(dayIndex === 0 ? 2 : dayIndex === 1 ? 3 : 0), L(0)];
    const templateId = await createTemplate(origin, `QA Label Row Copy ${Date.now()}`, days);
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await expectCell(page, 1, 0, 2);
      await expectCell(page, 1, 1, 'Empty');

      await dragLabel(page, rowLabel(page, 0), rowFrame(page, 1), { alt: true });
      await expect(page.getByTestId('label-drop-dialog')).toHaveCount(0);
      await expectCell(page, 1, 1, 2);
      await expectCell(page, 2, 1, 3);
      await expectCell(page, 3, 1, 'Empty');
      // The source row stays as it was.
      await expectCell(page, 1, 0, 2);
      await expectCell(page, 2, 0, 3);
      await expect(page.getByTestId('save-status')).toHaveText('Unsaved changes');
      await expect(page.getByTestId('plan-editor-announcer')).toHaveText('Breakfast copied to Lunch across the week.');

      await undo(page);
      await expectCell(page, 1, 1, 'Empty');
      await expectCell(page, 2, 1, 'Empty');
      await expect(page.getByTestId('save-status')).toHaveText('Saved');
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('a row dropped on another row moves its contents per day and shifts what lies between, and one Undo restores it', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    // Mon: B2 L3 D4. Tue: B5, Lunch empty, D6. Wed: B7 D8 (no Lunch). Thu: only L9.
    const days: DaySeed = (dayIndex) =>
      dayIndex === 0
        ? [B(2), L(3), D(4)]
        : dayIndex === 1
          ? [B(5), L(0), D(6)]
          : dayIndex === 2
            ? [B(7), D(8)]
            : dayIndex === 3
              ? [L(9)]
              : [];
    const templateId = await createTemplate(origin, `QA Label Row Move ${Date.now()}`, days);
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await expectCell(page, 1, 0, 2);
      await expectCell(page, 4, 1, 9);

      await dragLabel(page, rowLabel(page, 0), rowFrame(page, 2));
      await expect(page.getByTestId('label-drop-dialog')).toHaveCount(0);
      // Breakfast moves to Dinner and Lunch and Dinner shift up one row, on every day that has Breakfast and Dinner.
      await expectCell(page, 1, 0, 3);
      await expectCell(page, 1, 1, 4);
      await expectCell(page, 1, 2, 2);
      await expectCell(page, 2, 0, 'Empty');
      await expectCell(page, 2, 1, 6);
      await expectCell(page, 2, 2, 5);
      // Wednesday has no Lunch, so only its Breakfast and Dinner trade places, and it still has none.
      await expectCell(page, 3, 0, 8);
      await expectCell(page, 3, 2, 7);
      await expectCell(page, 3, 1, 'Empty');
      // Thursday lacks Breakfast and Dinner and stays as it is.
      await expectCell(page, 4, 1, 9);
      await expectCell(page, 4, 0, 'Empty');
      await expect(page.getByTestId('save-status')).toHaveText('Unsaved changes');
      await expect(page.getByTestId('plan-editor-announcer')).toHaveText('Breakfast moved to Dinner across the week.');

      await page.getByRole('button', { name: 'Save' }).click();
      await expect(page.getByTestId('save-status')).toHaveText('Saved');
      const kinds = await savedKinds(origin, templateId);
      expect(kinds[3]).toEqual(['Breakfast', 'Dinner']);
      expect(kinds[4]).toEqual(['Lunch']);

      await undo(page);
      await expectCell(page, 1, 0, 2);
      await expectCell(page, 1, 1, 3);
      await expectCell(page, 1, 2, 4);
      await expectCell(page, 3, 0, 7);
      await expectCell(page, 3, 2, 8);
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('a row Alt-dropped on a filled row asks, and Replace and Append each work out per day', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    // Mon: B2 L3. Tue: B4, Lunch empty. Wed: Breakfast empty, L5.
    const days: DaySeed = (dayIndex) =>
      dayIndex === 0 ? [B(2), L(3)] : dayIndex === 1 ? [B(4), L(0)] : dayIndex === 2 ? [B(0), L(5)] : [B(0), L(0)];
    const templateId = await createTemplate(origin, `QA Label Row Filled ${Date.now()}`, days);
    try {
      await page.goto(`/plan-templates/${templateId}`);
      const dialog = page.getByTestId('label-drop-dialog');
      const expectOriginal = async () => {
        await expectCell(page, 1, 0, 2);
        await expectCell(page, 1, 1, 3);
        await expectCell(page, 2, 0, 4);
        await expectCell(page, 2, 1, 'Empty');
        await expectCell(page, 3, 0, 'Empty');
        await expectCell(page, 3, 1, 5);
      };
      await expectOriginal();

      // The dialog offers no Swap: copying has Replace, Append and Cancel only.
      await dragLabel(page, rowLabel(page, 0), rowFrame(page, 1), { alt: true });
      await expect(dialog).toBeVisible();
      await expect(dialog.getByRole('button', { name: 'Swap' })).toHaveCount(0);
      await dialog.getByRole('button', { name: 'Cancel' }).click();
      await expectOriginal();

      // Replace: Lunch becomes an exact copy of Breakfast, so Wednesday's Lunch is cleared (its Breakfast is empty).
      await dragLabel(page, rowLabel(page, 0), rowFrame(page, 1), { alt: true });
      await dialog.getByRole('button', { name: 'Replace' }).click();
      await expectCell(page, 1, 0, 2);
      await expectCell(page, 1, 1, 2);
      await expectCell(page, 2, 0, 4);
      await expectCell(page, 2, 1, 4);
      await expectCell(page, 3, 0, 'Empty');
      await expectCell(page, 3, 1, 'Empty');
      await undo(page);
      await expectOriginal();
      await expect(page.getByTestId('save-status')).toHaveText('Saved');

      // Append: Breakfast's items join Lunch's; Tuesday's empty Lunch is filled; Wednesday's Lunch stays as it was.
      await dragLabel(page, rowLabel(page, 0), rowFrame(page, 1), { alt: true });
      await dialog.getByRole('button', { name: 'Append' }).click();
      await expectCell(page, 1, 0, 2);
      await expectCell(page, 1, 1, 5);
      await expectCell(page, 2, 0, 4);
      await expectCell(page, 2, 1, 4);
      await expectCell(page, 3, 0, 'Empty');
      await expectCell(page, 3, 1, 5);
      await undo(page);
      await expectOriginal();
      await expect(page.getByTestId('save-status')).toHaveText('Saved');
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('Cancel in the dialog changes nothing', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const templateId = await createTemplate(origin, `QA Label Row Cancel ${Date.now()}`, same([B(2), L(3)]));
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await dragLabel(page, rowLabel(page, 0), rowFrame(page, 1), { alt: true });
      const dialog = page.getByTestId('label-drop-dialog');
      await expect(dialog).toBeVisible();
      await dialog.getByRole('button', { name: 'Cancel' }).click();
      await expect(dialog).toHaveCount(0);
      await expectCell(page, 1, 0, 2);
      await expectCell(page, 1, 1, 3);
      await expect(page.getByTestId('save-status')).toHaveText('Saved');
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('a day that lacks the target kind gets that meal created by an Alt copy, and one Undo takes it away', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    // Tuesday has a Breakfast but no Lunch meal at all.
    const days: DaySeed = (dayIndex) => (dayIndex === 0 ? [B(2), L(0)] : dayIndex === 1 ? [B(3)] : []);
    const templateId = await createTemplate(origin, `QA Label Create ${Date.now()}`, days);
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await expectCell(page, 2, 0, 3);
      await expectCell(page, 2, 1, 'Empty');

      // Copy onto the empty Lunch row creates Tuesday's Lunch.
      await dragLabel(page, rowLabel(page, 0), rowFrame(page, 1), { alt: true });
      await expectCell(page, 1, 1, 2);
      await expectCell(page, 2, 1, 3);
      await expectCell(page, 2, 0, 3);
      await page.getByRole('button', { name: 'Save' }).click();
      await expect(page.getByTestId('save-status')).toHaveText('Saved');
      expect((await savedKinds(origin, templateId))[2]).toEqual(['Breakfast', 'Lunch']);

      await undo(page);
      await expectCell(page, 2, 1, 'Empty');
      await expectCell(page, 2, 0, 3);
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('a row move never creates a meal: a day that lacks the target row keeps its meals', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    // Monday has Breakfast and Lunch; Tuesday only a Breakfast.
    const days: DaySeed = (dayIndex) => (dayIndex === 0 ? [B(2), L(5)] : dayIndex === 1 ? [B(3)] : []);
    const templateId = await createTemplate(origin, `QA Label Row No Create ${Date.now()}`, days);
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await expectCell(page, 2, 0, 3);
      await expectCell(page, 2, 1, 'Empty');

      await dragLabel(page, rowLabel(page, 0), rowFrame(page, 1));
      await expectCell(page, 1, 0, 5);
      await expectCell(page, 1, 1, 2);
      await expectCell(page, 2, 0, 3);
      await expectCell(page, 2, 1, 'Empty');
      await page.getByRole('button', { name: 'Save' }).click();
      await expect(page.getByTestId('save-status')).toHaveText('Saved');
      const kinds = await savedKinds(origin, templateId);
      expect(kinds[2]).toEqual(['Breakfast']);
      expect(kinds[1]).toEqual(['Breakfast', 'Lunch']);
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('an Alt copy skips a day at 20 meals and names it, while the other days take the copy', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const triple = (kind: string): SeedMeal[] => [
      { kind, count: 0 },
      { kind, count: 0 },
      { kind, count: 0 },
    ];
    // Tuesday holds 20 meals: three of each kind but two Post-workout. It has no fourth Breakfast, which Monday does.
    const tuesday: SeedMeal[] = [
      { kind: 'Breakfast', count: 3 },
      { kind: 'Breakfast', count: 0 },
      { kind: 'Breakfast', count: 0 },
      ...triple('MorningSnack'),
      ...triple('Lunch'),
      ...triple('AfternoonSnack'),
      ...triple('Dinner'),
      ...triple('PreWorkout'),
      { kind: 'PostWorkout', count: 0 },
      { kind: 'PostWorkout', count: 0 },
    ];
    expect(tuesday).toHaveLength(MAX_MEALS_PER_DAY);
    const days: DaySeed = (dayIndex) =>
      dayIndex === 0 ? [B(2), B(0), B(0), B(0)] : dayIndex === 1 ? tuesday : [];
    const templateId = await createTemplate(origin, `QA Label Cap ${Date.now()}`, days);
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await expectCell(page, 1, 0, 2);
      await expectCell(page, 2, 0, 3);
      await expectCell(page, 1, 3, 'Empty');

      // Rows 0 and 3 are Breakfast and the fourth Breakfast: nothing is in the target, so no dialog.
      await dragLabel(page, rowLabel(page, 0), rowFrame(page, 3), { alt: true });
      await expect(page.getByTestId('label-drop-dialog')).toHaveCount(0);
      await expectCell(page, 1, 3, 2);
      // Tuesday would pass 20 meals, so it keeps its 20 and has no fourth Breakfast.
      await expectCell(page, 2, 3, 'Empty');
      const skipped = `Skipped, a day holds at most ${MAX_MEALS_PER_DAY} meals: Tuesday.`;
      await expect(page.getByTestId('plan-editor-announcer')).toHaveText(
        `Breakfast copied to Breakfast across the week. ${skipped}`,
      );
      await expect(page.locator('[data-slot="toast-viewport"]').getByText(skipped)).toBeVisible();
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('a day Alt-dropped on an empty day copies it, and one Undo restores it', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const templateId = await createTemplate(origin, `QA Label Day Copy ${Date.now()}`, (dayIndex) =>
      dayIndex === 0 ? [B(2), L(3)] : [],
    );
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await expectCell(page, 1, 0, 2);
      await expectCell(page, 2, 0, 'Empty');

      await dragLabel(page, dayLabel(page, 1), dayFrame(page, 2), { alt: true });
      await expect(page.getByTestId('label-drop-dialog')).toHaveCount(0);
      await expectCell(page, 2, 0, 2);
      await expectCell(page, 2, 1, 3);
      await expectCell(page, 1, 0, 2);
      await expectCell(page, 1, 1, 3);
      await expect(page.getByTestId('plan-editor-announcer')).toHaveText('Monday copied to Tuesday.');

      await undo(page);
      await expectCell(page, 2, 0, 'Empty');
      await expectCell(page, 2, 1, 'Empty');
      await expect(page.getByTestId('save-status')).toHaveText('Saved');
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('a day dropped on another day rotates the days between, carries the meal kinds along, and saves with unique ids', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    // Monday has Breakfast and Lunch, Tuesday only a Dinner, Wednesday a Breakfast; Monday moves onto Wednesday.
    const templateId = await createTemplate(origin, `QA Label Day Move ${Date.now()}`, (dayIndex) =>
      dayIndex === 0 ? [B(2), L(3)] : dayIndex === 1 ? [D(4)] : dayIndex === 2 ? [B(6)] : [],
    );
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await expectCell(page, 1, 0, 2);
      await expectCell(page, 2, 2, 4);

      await dragLabel(page, dayLabel(page, 1), dayFrame(page, 3));
      await expect(page.getByTestId('label-drop-dialog')).toHaveCount(0);
      // Monday takes Tuesday's meals, Tuesday takes Wednesday's, Wednesday takes Monday's.
      await expectCell(page, 1, 0, 'Empty');
      await expectCell(page, 1, 1, 'Empty');
      await expectCell(page, 1, 2, 4);
      await expectCell(page, 2, 0, 6);
      await expectCell(page, 2, 1, 'Empty');
      await expectCell(page, 2, 2, 'Empty');
      await expectCell(page, 3, 0, 2);
      await expectCell(page, 3, 1, 3);
      await expectCell(page, 3, 2, 'Empty');
      await expect(page.getByTestId('plan-editor-announcer')).toHaveText('Monday moved to Wednesday.');

      const put = page.waitForRequest(
        (request) => request.url().includes('/nutrition/plan-templates/') && request.method() === 'PUT',
      );
      await page.getByRole('button', { name: 'Save' }).click();
      // The saved week never carries a meal id twice, which the server would answer with a 400.
      const sent = (await put).postDataJSON() as SavedMealsBody;
      const ids = (sent.weeks?.[0]?.days ?? []).flatMap((day) => (day.meals ?? []).map((meal) => meal.mealId ?? ''));
      expect(ids.length).toBe(4);
      expect(ids.every((id) => id !== '')).toBe(true);
      expect(new Set(ids).size).toBe(ids.length);
      await expect(page.getByTestId('save-status')).toHaveText('Saved');
      const kinds = await savedKinds(origin, templateId);
      expect(kinds[1]).toEqual(['Dinner']);
      expect(kinds[2]).toEqual(['Breakfast']);
      expect(kinds[3]).toEqual(['Breakfast', 'Lunch']);

      await undo(page);
      await expectCell(page, 1, 0, 2);
      await expectCell(page, 2, 2, 4);
      await expectCell(page, 3, 0, 6);
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('the remove button and the day note button sit outside the drag handle and still work', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    // Lunch is empty on every day, so removing its row asks nothing.
    const templateId = await createTemplate(origin, `QA Label Buttons ${Date.now()}`, (dayIndex) =>
      dayIndex === 0 ? [B(2), L(0)] : [B(0), L(0)],
    );
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await expect(page.getByTestId('row-label')).toHaveCount(2);
      // Only the label text is the handle: neither button is inside it.
      await expect(rowLabel(page, 1).getByTestId('remove-row')).toHaveCount(0);
      await expect(dayLabel(page, 1).getByTestId('day-note-button')).toHaveCount(0);

      const noteButton = page.getByRole('button', { name: 'Edit note for Monday' });
      await noteButton.click();
      const popover = page.getByTestId('day-note-popover');
      await popover.getByLabel('Day note for Monday').fill('Cook the rice the night before');
      await page.keyboard.press('Escape');
      await expect(popover).toHaveCount(0);
      await expect(noteButton).toHaveAttribute('data-has-note', 'true');

      await rowFrame(page, 1).getByTestId('remove-row').click();
      await expect(page.getByTestId('row-label')).toHaveCount(1);
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('while a label is dragged the whole target row lights up, and a drop anywhere in it moves the row', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const templateId = await createTemplate(origin, `QA Label Highlights ${Date.now()}`, (dayIndex) =>
      dayIndex === 0 ? [B(2), L(3)] : [],
    );
    try {
      await page.goto(`/plan-templates/${templateId}`);
      await expectCell(page, 1, 0, 2);

      await beginDrag(page, rowLabel(page, 0));
      // The dragged row's seven tiles are the source; nothing is a target yet, and no insert line is in the way.
      await expect(page.locator('[data-band="source"]')).toHaveCount(7);
      await expect(page.locator('[data-band="target"]')).toHaveCount(0);
      await expect(page.locator('[data-over="true"]')).toHaveCount(0);
      await expect(page.getByTestId('meal-insert-line')).toHaveCount(0);

      // Over a tile of another row: the whole row, label included, is the target.
      const box = await settledBox(cell(page, 1, 1));
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2, { steps: 12 });
      await expect(page.locator('[data-band="target"]')).toHaveCount(7);
      await expect(cell(page, 7, 1)).toHaveAttribute('data-band', 'target');
      await expect(rowFrame(page, 1)).toHaveAttribute('data-over', 'true');
      await expect(page.locator('[data-over="true"]')).toHaveCount(1);
      // The preview already shows the move: Breakfast's contents sit in the Lunch row.
      await expectCell(page, 1, 1, 2);
      await expectCell(page, 1, 0, 3);

      // A day label is not a target for a row: over the day names nothing is lit, and the preview goes back.
      const dayBox = await settledBox(dayFrame(page, 2));
      await page.mouse.move(dayBox.x + dayBox.width / 2, dayBox.y + dayBox.height / 2, { steps: 12 });
      await expect(page.locator('[data-band="target"]')).toHaveCount(0);
      await expect(page.locator('[data-over="true"]')).toHaveCount(0);
      await expectCell(page, 1, 0, 2);

      // Released over a tile of the target row the drop lands and moves the row, without a dialog.
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2, { steps: 12 });
      await expect(page.locator('[data-band="target"]')).toHaveCount(7);
      await page.mouse.up();
      await expect(page.locator('[data-band]')).toHaveCount(0);
      await expect(page.getByTestId('label-drop-dialog')).toHaveCount(0);
      await expectCell(page, 1, 0, 3);
      await expectCell(page, 1, 1, 2);
      await expect(page.getByTestId('save-status')).toHaveText('Unsaved changes');
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('Alt+ArrowUp and Alt+ArrowDown on a row label move it one row, announce it, and stop at the ends', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const templateId = await createTemplate(origin, `QA Label Row Keys ${Date.now()}`, (dayIndex) =>
      dayIndex === 0 ? [B(2), L(3)] : dayIndex === 1 ? [B(4), L(5)] : [B(0), L(0)],
    );
    try {
      await page.goto(`/plan-templates/${templateId}`);
      const announcer = page.getByTestId('plan-editor-announcer');
      await expect(rowLabel(page, 0)).toHaveAttribute('aria-keyshortcuts', 'Alt+ArrowUp Alt+ArrowDown');
      await expectCell(page, 1, 0, 2);

      await rowLabel(page, 0).focus();
      await page.keyboard.press('Alt+ArrowDown');
      await expectCell(page, 1, 0, 3);
      await expectCell(page, 1, 1, 2);
      await expectCell(page, 2, 0, 5);
      await expectCell(page, 2, 1, 4);
      await expect(rowLabel(page, 1)).toBeFocused();
      await expect(announcer).toHaveText('Breakfast moved to Lunch across the week.');

      // One Undo takes back the one move.
      await undo(page);
      await expectCell(page, 1, 0, 2);
      await expectCell(page, 1, 1, 3);
      await expect(page.getByTestId('save-status')).toHaveText('Saved');

      // At the first row Alt+ArrowUp does nothing: no edit, no new announcement.
      await rowLabel(page, 0).focus();
      await page.keyboard.press('Alt+ArrowUp');
      await expectCell(page, 1, 0, 2);
      await expect(page.getByTestId('save-status')).toHaveText('Saved');
      await expect(announcer).toHaveText('Breakfast moved to Lunch across the week.');

      // The same at the last row going down.
      await rowLabel(page, 1).focus();
      await page.keyboard.press('Alt+ArrowDown');
      await expectCell(page, 1, 1, 3);
      await expect(page.getByTestId('save-status')).toHaveText('Saved');
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('Alt+ArrowLeft and Alt+ArrowRight on a day label move it one day and stop at the ends', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const templateId = await createTemplate(origin, `QA Label Day Keys ${Date.now()}`, (dayIndex) =>
      dayIndex === 0 ? [B(2), L(3)] : dayIndex === 1 ? [B(4), L(5)] : [],
    );
    try {
      await page.goto(`/plan-templates/${templateId}`);
      const announcer = page.getByTestId('plan-editor-announcer');
      await expect(dayLabel(page, 1)).toHaveAttribute('aria-keyshortcuts', 'Alt+ArrowLeft Alt+ArrowRight');
      await expectCell(page, 1, 0, 2);

      await dayLabel(page, 1).focus();
      await page.keyboard.press('Alt+ArrowRight');
      await expectCell(page, 1, 0, 4);
      await expectCell(page, 1, 1, 5);
      await expectCell(page, 2, 0, 2);
      await expectCell(page, 2, 1, 3);
      await expect(dayLabel(page, 2)).toBeFocused();
      await expect(announcer).toHaveText('Monday moved to Tuesday.');

      await undo(page);
      await expectCell(page, 1, 0, 2);
      await expectCell(page, 2, 0, 4);
      await expect(page.getByTestId('save-status')).toHaveText('Saved');

      // Monday is the first day and Sunday the last: Alt+ArrowLeft and Alt+ArrowRight there do nothing.
      await dayLabel(page, 1).focus();
      await page.keyboard.press('Alt+ArrowLeft');
      await dayLabel(page, 7).focus();
      await page.keyboard.press('Alt+ArrowRight');
      await expectCell(page, 1, 0, 2);
      await expect(page.getByTestId('save-status')).toHaveText('Saved');
      await expect(announcer).toHaveText('Monday moved to Tuesday.');
    } finally {
      await deleteTemplate(origin, templateId);
    }
  });

  test('the client plan editor Alt-drags a row label to copy it too', async ({ page, baseURL }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    // Dinner is empty everywhere, so Monday has no Dinner meal yet and the copy creates it.
    const planId = await createClientPlan(
      origin,
      `QA Label Client ${Date.now()}`,
      (dayIndex) => (dayIndex === 0 ? [B(2), L(3)] : dayIndex === 1 ? [D(0)] : []),
      'draft',
    );
    try {
      await page.goto(`/clients/${CLIENT_PROFILE_ID}/nutrition/plans/${planId}`);
      await expectCell(page, 1, 0, 2);
      await expectCell(page, 1, 2, 'Empty');

      await dragLabel(page, rowLabel(page, 0), rowFrame(page, 2), { alt: true });
      await expect(page.getByTestId('label-drop-dialog')).toHaveCount(0);
      await expectCell(page, 1, 2, 2);
      await expectCell(page, 1, 0, 2);
      await expect(page.getByTestId('save-status')).toHaveText('Unsaved changes');

      await undo(page);
      await expectCell(page, 1, 2, 'Empty');
    } finally {
      await deletePlan(origin, planId);
    }
  });

  test('a completed client plan is read-only: the labels are plain text with no drag or shortcuts', async ({
    page,
    baseURL,
  }) => {
    const origin = baseURL ?? 'http://localhost:5173';
    const planId = await createClientPlan(
      origin,
      `QA Label ReadOnly ${Date.now()}`,
      (dayIndex) => (dayIndex === 0 ? [B(2), L(3)] : []),
      'completed',
    );
    try {
      await page.goto(`/clients/${CLIENT_PROFILE_ID}/nutrition/plans/${planId}`);
      await expect(page.getByRole('note')).toBeVisible();
      await expectCell(page, 1, 0, 2);

      await expect(page.getByTestId('row-label')).toHaveCount(0);
      await expect(page.getByTestId('day-label')).toHaveCount(0);
      await expect(page.locator('[aria-keyshortcuts]')).toHaveCount(0);
      await expect(page.getByTestId('row-label-frame')).toHaveCount(0);
      await expect(page.getByRole('group', { name: 'Week meals' }).getByText('Breakfast', { exact: true }).first()).toBeVisible();
    } finally {
      await deletePlan(origin, planId);
    }
  });
});
