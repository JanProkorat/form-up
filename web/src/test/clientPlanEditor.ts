/**
 * Shared harness for the client plan editor tests (`renderPlanEditor.tsx` mounts the page): serves the plan,
 * the client, the offline-sync conflicts list and the review / save calls from one mutable `PlanEditorState`. A test changes the state (or adds its own `server.use(...)` handler, which wins over these)
 * and then asserts on what the coach sees. Payloads are typed with the generated API types.
 *
 * Dates are relative to `EDITOR_NOW`, which a test pins with `pinEditorNow()`: "today" (the client's day) is a
 * Wednesday, so `pastStart()` is the Monday before this week and `futureStart()` is twelve weeks ahead.
 */
import { vi } from 'vitest';
import {
  MealKind,
  MealLockState,
  MealItemConflictTag,
  OfflineSyncConflictKind,
  SeenMealItemKind,
  WeekStatus,
} from '@/api/generated';
import type {
  EatenMealItem,
  GetClientOfflineSyncConflictsResponse,
  GetPlanResponse,
  MealLockDto,
  OfflineSyncConflictDto,
  SearchFoodsResponse,
  SearchRecipesResponse,
} from '@/api/generated';
import { CLIENT_ID, COACH_USER_ID, newClientDetailState, serveClientDetail } from '@/test/clientDetail';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

export const PLAN_ID = '55555555-5555-5555-5555-555555555555';
export const PLAN_NAME = 'QA Plan';
export const MEAL_ID = '66666666-6666-6666-6666-666666666666';
export const FOOD_ITEM_ID = '77777777-7777-7777-7777-777777777777';
export const FOOD_ID = '88888888-8888-8888-8888-888888888888';
export const FOOD_NAME = 'Apple';
export const BANANA_ITEM_ID = 'bbbbbbbb-0000-0000-0000-000000000001';
export const ALMOND_ITEM_ID = 'bbbbbbbb-0000-0000-0000-000000000002';
export const GHOST_MEAL_ID = '22222222-2222-2222-2222-222222222222';

const DAY_MS = 24 * 60 * 60 * 1000;

/** A Wednesday; "now" for the whole editor. */
export const EDITOR_NOW = Date.UTC(2026, 5, 17, 12, 0, 0);

/** Fakes only Date, so timers, msw and Testing Library's polling keep running for real. */
export function pinEditorNow(): void {
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(EDITOR_NOW);
}

const isoDay = (ms: number) => new Date(ms).toISOString().slice(0, 10);
const todayMs = Date.UTC(2026, 5, 17);
const thisMondayMs = todayMs - 2 * DAY_MS;

/** The Monday of last week: every day of week 1 is before today. */
export const pastStart = () => isoDay(thisMondayMs - 7 * DAY_MS);
/** The Monday of this week: Monday and Tuesday are past, Wednesday is today. */
export const thisWeekStart = () => isoDay(thisMondayMs);
/** A Monday twelve weeks ahead: nothing is past or eaten. */
export const futureStart = () => isoDay(thisMondayMs + 12 * 7 * DAY_MS);
export const dayAfter = (date: string) => isoDay(Date.parse(`${date}T00:00:00Z`) + DAY_MS);

export interface PlanEditorState {
  /** The Monday of week 1, as the editor is told it. */
  startDate: string;
  /** Lock verdicts patched into the plan response; they carry the snapshots and item tags. */
  locks: MealLockDto[];
  /** The unreviewed conflicts the list answers with; null answers 403. */
  conflicts: OfflineSyncConflictDto[] | null;
  /** Status the single review call answers with; 204 removes the conflict from the list. */
  reviewStatus: number;
  /** Conflict ids the page POSTed `/review` for, in order. */
  reviewed: string[];
  /** Bodies of the `review-all` calls. */
  reviewAllBodies: unknown[];
  /** Save answers the lock 409 once, then success. */
  rejectSaveOnce: boolean;
  saves: number;
  version: number;
}

export function newPlanEditorState(overrides: Partial<PlanEditorState> = {}): PlanEditorState {
  return {
    startDate: futureStart(),
    locks: [],
    conflicts: [],
    reviewStatus: 204,
    reviewed: [],
    reviewAllBodies: [],
    rejectSaveOnce: false,
    saves: 0,
    version: 3,
    ...overrides,
  };
}

/** A one-week draft plan with one Breakfast (one food, 100 g) on Monday, as the plan GET returns it. */
export function editorPlan(state: PlanEditorState): GetPlanResponse {
  return {
    planId: PLAN_ID,
    clientId: CLIENT_ID,
    nutritionistId: COACH_USER_ID,
    name: PLAN_NAME,
    status: 'Draft',
    globalSettings: { dailyKcal: 2000 },
    version: state.version,
    dateCreated: '2026-01-01T00:00:00Z',
    startDate: state.startDate,
    clientToday: isoDay(todayMs),
    mealLogs: [],
    supplements: [],
    mealLocks: state.locks,
    weeks: [
      {
        weekNumber: 1,
        status: WeekStatus.Draft,
        days: Array.from({ length: 7 }, (_, index) => ({
          dayOfWeek: index + 1,
          meals:
            index === 0
              ? [
                  {
                    mealId: MEAL_ID,
                    kind: MealKind.Breakfast,
                    order: 1,
                    recipes: [],
                    foods: [
                      {
                        itemId: FOOD_ITEM_ID,
                        foodExternalId: FOOD_ID,
                        foodName: FOOD_NAME,
                        amountGrams: 100,
                        nutrientValuePer100Grams: { kcal: 52, protein: 0.3, carbs: 14, fat: 0.2 },
                      },
                    ],
                  },
                ]
              : [],
        })),
      },
    ],
  };
}

const apple = (amount: number, kcal: number): EatenMealItem => ({
  itemId: FOOD_ITEM_ID,
  kind: SeenMealItemKind.Food,
  refId: FOOD_ID,
  name: FOOD_NAME,
  amount,
  kcal,
});

/** The lock verdict of the plan's Monday breakfast (eaten: the apple at 150 g and a banana). `over` replaces any field. */
export function mealLock(state: Pick<PlanEditorState, 'startDate'>, over: Partial<MealLockDto> = {}): MealLockDto {
  return {
    mealId: MEAL_ID,
    weekNumber: 1,
    dayOfWeek: 1,
    date: state.startDate,
    isLocked: true,
    lockState: MealLockState.Eaten,
    isRemovedFromPlan: false,
    mealKind: MealKind.Breakfast,
    eatenItemIds: [FOOD_ITEM_ID, BANANA_ITEM_ID],
    eatenSnapshot: [
      apple(150, 80),
      {
        itemId: BANANA_ITEM_ID,
        kind: SeenMealItemKind.Food,
        refId: '11111111-1111-1111-1111-111111111111',
        name: 'Banana <b>bold</b>',
        amount: 120,
        kcal: 107,
      },
    ],
    ...over,
  };
}

/** The Monday breakfast eaten offline while the coach changed the apple and removed the banana. */
export function conflictedLock(state: Pick<PlanEditorState, 'startDate'>, over: Partial<MealLockDto> = {}): MealLockDto {
  return mealLock(state, {
    conflict: {
      conflictId: 'c-1',
      kind: OfflineSyncConflictKind.PlanChanged,
      basedOnPlanVersion: 4,
      currentPlanVersion: 6,
      reviewed: false,
      itemTags: [
        { itemId: FOOD_ITEM_ID, tag: MealItemConflictTag.Changed },
        { itemId: BANANA_ITEM_ID, tag: MealItemConflictTag.Removed },
      ],
    },
    ...over,
  });
}

/** The Tuesday snack the client ate offline and the coach then removed from the plan. */
export function removedLock(startDate: string): MealLockDto {
  return {
    mealId: GHOST_MEAL_ID,
    weekNumber: 1,
    dayOfWeek: 2,
    date: dayAfter(startDate),
    isLocked: true,
    lockState: MealLockState.Eaten,
    isRemovedFromPlan: true,
    mealKind: MealKind.AfternoonSnack,
    eatenItemIds: [ALMOND_ITEM_ID],
    eatenSnapshot: [
      {
        itemId: ALMOND_ITEM_ID,
        kind: SeenMealItemKind.Food,
        refId: '33333333-3333-3333-3333-333333333333',
        name: 'Almonds',
        amount: 30,
        kcal: 130,
      },
    ],
    conflict: {
      conflictId: 'c-ghost',
      kind: OfflineSyncConflictKind.MealRemoved,
      basedOnPlanVersion: 4,
      currentPlanVersion: 6,
      reviewed: false,
      itemTags: [{ itemId: ALMOND_ITEM_ID, tag: MealItemConflictTag.Removed }],
    },
  };
}

/** A listed conflict on the plan's Monday; `over` replaces any field. */
export function listedConflict(
  state: Pick<PlanEditorState, 'startDate'>,
  over: Partial<OfflineSyncConflictDto> = {},
): OfflineSyncConflictDto {
  return {
    conflictId: 'c-1',
    planId: PLAN_ID,
    planName: PLAN_NAME,
    operation: 'LogMealEaten',
    kind: OfflineSyncConflictKind.PlanChanged,
    mealId: MEAL_ID,
    mealKind: MealKind.Breakfast,
    mealLogged: true,
    occurredAt: `${state.startDate}T09:40:00Z`,
    logDate: state.startDate,
    basedOnPlanVersion: 4,
    currentPlanVersion: 6,
    detectedAt: new Date(EDITOR_NOW).toISOString(),
    ...over,
  };
}

const conflictsBase = apiUrl(`/trainer/clients/${CLIENT_ID}/offline-sync-conflicts`);

/** Serves the editor's own requests from `state`; the client page's shared requests come from `serveClientDetail`. */
export function servePlanEditor(state: PlanEditorState): void {
  serveClientDetail(newClientDetailState());
  // The last plan served, so a successful save can answer with it.
  let lastPlan: GetPlanResponse = editorPlan(state);
  // The editor's side library loads its first page of recipes and foods; these tests don't use it.
  const noRecipes: SearchRecipesResponse = { recipes: [], totalCount: 0, page: 1, pageSize: 25 };
  const noFoods: SearchFoodsResponse = { foods: [], totalCount: 0, page: 1, pageSize: 25 };
  server.use(
    http.get(apiUrl('/recipes'), () => HttpResponse.json(noRecipes)),
    http.get(apiUrl('/foods/search'), () => HttpResponse.json(noFoods)),
    http.get(apiUrl(`/nutrition/plans/${PLAN_ID}`), () => {
      lastPlan = editorPlan(state);
      return HttpResponse.json(lastPlan);
    }),
    http.put(apiUrl(`/nutrition/plans/${PLAN_ID}`), () => {
      state.saves += 1;
      if (state.rejectSaveOnce && state.saves === 1) {
        return HttpResponse.json(
          { title: 'Locked', status: 409, errorCode: 'MEAL_LOCKED', mealId: MEAL_ID, weekNumber: 1, dayOfWeek: 1 },
          { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
        );
      }
      return HttpResponse.json({ ...lastPlan, version: (lastPlan.version ?? 1) + state.saves });
    }),
    http.get(conflictsBase, () => {
      if (state.conflicts === null) {
        return HttpResponse.json({ title: 'Forbidden' }, { status: 403 });
      }
      const body: GetClientOfflineSyncConflictsResponse = { items: state.conflicts };
      return HttpResponse.json(body);
    }),
    http.post(`${conflictsBase}/:conflictId/review`, ({ params }) => {
      const conflictId = String(params['conflictId']);
      state.reviewed.push(conflictId);
      if (state.reviewStatus !== 204) {
        return HttpResponse.json({ title: 'boom' }, { status: state.reviewStatus });
      }
      state.conflicts = (state.conflicts ?? []).filter((item) => item.conflictId !== conflictId);
      return new HttpResponse(null, { status: 204 });
    }),
    http.post(`${conflictsBase}/review-all`, async ({ request }) => {
      state.reviewAllBodies.push(await request.json());
      state.conflicts = [];
      return HttpResponse.json({ reviewedCount: 1 });
    }),
  );
}
