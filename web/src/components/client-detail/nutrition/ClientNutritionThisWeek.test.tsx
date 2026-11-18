/**
 * Client detail, Nutrition tab: the "This week" card. Ported from the Playwright spec
 * (nutritionist/client-detail-nutrition): the numbers come from one faked plan with "now" pinned to
 * Thursday noon, so Monday to Wednesday are the finished days and Thursday is excluded.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import { MealEatenState, MealKind, WeekStatus } from '@/api/generated';
import type { GetPlanResponse, MealEatenStatusDto, PlanMeal } from '@/api/generated';
import { addPlan, newClientDetailState, nutritionPlan, renderNutritionTab, serveClientDetail } from '@/test/clientDetail';

afterEach(() => {
  vi.useRealTimers();
});

const WEEK_PLAN_ID = '00000000-0000-0000-1333-000000000001';
const WEEK_PLAN_NAME = 'Stub week plan';

const MONDAY = new Date(2026, 5, 8);
const MS_PER_DAY = 24 * 60 * 60 * 1000;

function localIso(date: Date): string {
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${date.getFullYear()}-${month}-${day}`;
}

/** Calendar date `offset` days from this week's Monday. */
function dayIso(offset: number): string {
  return localIso(new Date(MONDAY.getFullYear(), MONDAY.getMonth(), MONDAY.getDate() + offset));
}

/** Pins "now" to this week's Thursday noon. */
function pinThursday() {
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(new Date(MONDAY.getTime() + 3 * MS_PER_DAY + 12 * 60 * 60 * 1000));
}

type Macros = { kcal: number; protein: number; carbs: number; fat: number };

/** A meal with stored totals, or (null) one without, so the foods fallback is used. */
function meal(id: string, kind: MealKind, order: number, macros: Macros | null): PlanMeal {
  return {
    mealId: id,
    kind,
    order,
    foods: macros
      ? []
      : [
          {
            foodExternalId: 'stub-food',
            foodName: 'Stub food',
            nutrientValuePer100Grams: { kcal: 200, protein: 10, carbs: 20, fat: 5 },
            amountGrams: 100,
          },
        ],
    recipes: [],
    mealTotals: macros ? { ...macros, fiber: 0 } : undefined,
  };
}

/** Four meals a day; Monday's snack has no stored totals so the foods fallback is used. */
function dayMeals(prefix: string, snackTotals: boolean): PlanMeal[] {
  return [
    meal(`${prefix}-b`, MealKind.Breakfast, 1, { kcal: 500, protein: 30, carbs: 50, fat: 15 }),
    meal(`${prefix}-s`, MealKind.MorningSnack, 2, snackTotals ? { kcal: 200, protein: 10, carbs: 20, fat: 5 } : null),
    meal(`${prefix}-l`, MealKind.Lunch, 3, { kcal: 700, protein: 50, carbs: 70, fat: 20 }),
    meal(`${prefix}-d`, MealKind.Dinner, 4, { kcal: 600, protein: 40, carbs: 60, fat: 25 }),
  ];
}

function eaten(mealId: string, offset: number): MealEatenStatusDto {
  return {
    mealId,
    logDate: dayIso(offset),
    isEaten: true,
    eatenState: MealEatenState.Eaten,
    eatenAt: `${dayIso(offset)}T12:00:00Z`,
  };
}

/** Week 1 starting this Monday, with the eaten logs the numbers are computed from. */
function buildPlan(startDate: string): GetPlanResponse {
  return nutritionPlan(WEEK_PLAN_ID, {
    name: WEEK_PLAN_NAME,
    globalSettings: { dailyKcal: 2000, proteinGrams: 150, carbsGrams: 200, fatGrams: 60 },
    weeks: [
      {
        weekNumber: 1,
        status: WeekStatus.Published,
        days: [
          { dayOfWeek: 1, meals: dayMeals('mon', false) },
          { dayOfWeek: 2, meals: dayMeals('tue', true) },
          { dayOfWeek: 3, meals: dayMeals('wed', true) },
          { dayOfWeek: 4, meals: dayMeals('thu', true) },
        ],
      },
    ],
    startDate,
    mealLogs: [
      // Monday: everything eaten.
      eaten('mon-b', 0),
      eaten('mon-s', 0),
      eaten('mon-l', 0),
      eaten('mon-d', 0),
      // Tuesday: dinner only has a photo stub, which does not count.
      eaten('tue-b', 1),
      eaten('tue-s', 1),
      eaten('tue-l', 1),
      { mealId: 'tue-d', logDate: dayIso(1), isEaten: false, eatenState: MealEatenState.NotEaten },
      // Wednesday: breakfast only, a low day.
      eaten('wed-b', 2),
      // Today is excluded from the numbers.
      eaten('thu-b', 3),
      // A log on the right meal but another date does not count.
      { mealId: 'wed-l', logDate: dayIso(-7), isEaten: true, eatenState: MealEatenState.Eaten },
    ],
  });
}

/** Serves an Active plan starting on `startDate`; `plan` is read per request so a test can tweak or fail it. */
function serveWeekPlan(startDate: string, plan: () => GetPlanResponse | number = () => buildPlan(startDate)) {
  const state = newClientDetailState();
  serveClientDetail(state);
  addPlan(
    state,
    { planId: WEEK_PLAN_ID, name: WEEK_PLAN_NAME, periodStart: startDate, status: 'Active' },
    plan,
  );
  return state;
}

async function findCard() {
  return screen.findByRole('region', { name: 'This week' });
}

describe('Nutrition tab: This week card', () => {
  it('computes the week from the plan, excluding today', async () => {
    pinThursday();
    serveWeekPlan(dayIso(0));
    renderNutritionTab();

    const card = await findCard();
    expect(await within(card).findByText('Week 1 · Mon – Wed logged')).toBeInTheDocument();

    // 12 planned meals on Mon-Wed, 8 eaten (a stub log, another date and today do not count).
    expect(within(card).getByTestId('this-week-eaten')).toHaveTextContent(/^8$/);
    expect(card).toHaveTextContent('of 12 meals');
    expect(card).toHaveTextContent('ticked off · 67 %');

    const tiles = within(within(card).getByRole('list', { name: 'Meals ticked off per day' })).getAllByRole('listitem');
    expect(tiles).toHaveLength(7);
    const expected: [string, string, string | null][] = [
      ['past', 'false', '4/4'],
      ['past', 'false', '3/4'],
      ['past', 'true', '1/4'],
      ['today', 'false', 'today'],
      ['future', 'false', null],
      ['future', 'false', null],
      ['future', 'false', null],
    ];
    expected.forEach(([state, low, label], index) => {
      const tile = tiles[index];
      expect(tile).toHaveAttribute('data-state', state);
      expect(tile).toHaveAttribute('data-low', low);
      if (label) {
        expect(tile).toHaveTextContent(label);
      } else {
        // A future tile is empty and is not mistaken for today.
        expect(tile).not.toHaveTextContent('today');
        expect(tile).not.toHaveTextContent('/');
      }
    });

    // By meal: Snack groups the snack. Skipped 0 says "always", skipped 1 has no note,
    // skipped 2 or more is flagged red with the note.
    const row = (group: string) => {
      const element = card.querySelector<HTMLElement>(`[data-group="${group}"]`);
      if (!element) {
        throw new Error(`No row for ${group}`);
      }
      return element;
    };
    expect(row('breakfast')).toHaveTextContent('3/3');
    expect(row('breakfast')).toHaveTextContent('always');
    expect(row('breakfast')).toHaveAttribute('data-flagged', 'false');
    for (const group of ['snack', 'lunch']) {
      expect(row(group)).toHaveTextContent('2/3');
      expect(row(group)).toHaveAttribute('data-flagged', 'false');
      expect(row(group)).not.toHaveTextContent('skipped');
      expect(row(group)).not.toHaveTextContent('always');
    }
    expect(row('dinner')).toHaveTextContent('1/3');
    expect(row('dinner')).toHaveAttribute('data-flagged', 'true');
    expect(row('dinner')).toHaveTextContent('skipped 2× this week');

    // Daily averages over the three finished days against the plan targets.
    const macro = (name: string) => {
      const element = card.querySelector<HTMLElement>(`[data-macro="${name}"]`);
      if (!element) {
        throw new Error(`No macro ${name}`);
      }
      return element;
    };
    expect(macro('kcal')).toHaveTextContent('1,300');
    expect(macro('kcal')).toHaveTextContent('/ 2,000');
    expect(macro('protein')).toHaveTextContent('83');
    expect(macro('protein')).toHaveTextContent('/ 150 g');
    expect(macro('carbs')).toHaveTextContent('130');
    expect(macro('fat')).toHaveTextContent('40');
  });

  it('counts a partly eaten meal apart and takes its macros and share from the eaten items', async () => {
    pinThursday();
    // Tuesday's dinner gets two items; only the first is eaten, so it is partly eaten.
    const food = (itemId: string) => ({
      itemId,
      foodExternalId: 'stub-food',
      foodName: 'Stub food',
      nutrientValuePer100Grams: { kcal: 200, protein: 10, carbs: 20, fat: 5 },
      amountGrams: 100,
    });
    serveWeekPlan(dayIso(0), () => {
      const plan = buildPlan(dayIso(0));
      return {
        ...plan,
        weeks: (plan.weeks ?? []).map((week) => ({
          ...week,
          days: (week.days ?? []).map((day) => ({
            ...day,
            meals: (day.meals ?? []).map((m) =>
              m.mealId === 'tue-d' ? { ...m, foods: [food('item-1'), food('item-2')], mealTotals: undefined } : m,
            ),
          })),
        })),
        mealLogs: (plan.mealLogs ?? []).map((log) =>
          log.mealId === 'tue-d' ? { ...log, eatenState: MealEatenState.PartlyEaten, eatenItemIds: ['item-1'] } : log,
        ),
      };
    });
    renderNutritionTab();

    const card = await findCard();
    expect(await within(card).findByTestId('this-week-eaten')).toHaveTextContent(/^8$/);
    expect(within(card).getByTestId('this-week-partly')).toHaveTextContent(/^1 meal partly eaten$/);
    // 8 eaten meals plus half of the partly eaten one, of 12.
    expect(card).toHaveTextContent('ticked off · 71 %');

    const tuesday = within(within(card).getByRole('list', { name: 'Meals ticked off per day' })).getAllByRole('listitem')[1];
    expect(tuesday).toHaveTextContent('3+1/4');

    // Dinner: 1 eaten, 1 partly eaten, 1 skipped, so it is no longer flagged.
    const dinner = card.querySelector<HTMLElement>('[data-group="dinner"]');
    expect(dinner).toHaveTextContent('1/3');
    expect(dinner).toHaveTextContent('1× partly eaten');
    expect(dinner).toHaveAttribute('data-flagged', 'false');

    // Only the eaten item of the partly eaten meal adds to the averages (200 kcal, 10 g protein).
    expect(card.querySelector('[data-macro="kcal"]')).toHaveTextContent('1,367');
    expect(card.querySelector('[data-macro="protein"]')).toHaveTextContent('87');
  });

  it('shows a retry when the plan fails to load and recovers', async () => {
    pinThursday();
    let failing = true;
    serveWeekPlan(dayIso(0), () => (failing ? 500 : buildPlan(dayIso(0))));
    const { user } = renderNutritionTab();

    const card = await findCard();
    expect(await within(card).findByText("Couldn't load this week's meals.")).toBeInTheDocument();
    failing = false;
    await user.click(within(card).getByRole('button', { name: 'Retry' }));
    expect(await within(card).findByTestId('this-week-eaten')).toHaveTextContent(/^8$/);
  });

  it('says why there is nothing to track before the plan starts', async () => {
    pinThursday();
    serveWeekPlan(dayIso(7));
    renderNutritionTab();

    const card = await findCard();
    expect(await within(card).findByRole('status')).toHaveTextContent('The plan starts');
    expect(within(card).queryByTestId('this-week-eaten')).not.toBeInTheDocument();
  });

  it('says the plan has ended once today is past its last week', async () => {
    pinThursday();
    serveWeekPlan(dayIso(-21));
    renderNutritionTab();

    const card = await findCard();
    expect(await within(card).findByRole('status')).toHaveTextContent(/^The plan has ended\.$/);
    expect(within(card).queryByTestId('this-week-eaten')).not.toBeInTheDocument();
  });
});
