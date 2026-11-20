/**
 * Client detail, Nutrition tab: the "Is it working?" card and the nutrition profile card with its edit dialog.
 * Ported from the Playwright spec (nutritionist/client-detail-nutrition). "Now" is pinned to Wednesday
 * 10 June 2026, so the eight weeks start on Monday 20 April.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import { WeekStatus } from '@/api/generated';
import type {
  ClientPlanItem,
  MeasurementDto,
  OnboardingDataDto,
  UpdateClientNutritionProfileResponse,
} from '@/api/generated';
import {
  CLIENT_ID,
  addPlan,
  clientDashboard,
  newClientDetailState,
  nutritionPlan,
  renderNutritionTab,
  serveClientDetail,
} from '@/test/clientDetail';
import type { ClientDetailState } from '@/test/clientDetail';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

afterEach(() => {
  vi.useRealTimers();
});

const OLD_PLAN_ID = '00000000-0000-0000-1335-000000000001';
const NEW_PLAN_ID = '00000000-0000-0000-1335-000000000002';

const FULL_ONBOARDING: OnboardingDataDto = {
  targetWeightKg: 75,
  adjustedKcal: 2200,
  proteinGrams: 150,
  carbsGrams: 220,
  fatGrams: 70,
  fibreGrams: 30,
  allergies: 'gluten,lactose,none',
  dislikes: ['Mushrooms'],
  habits: ['Skips breakfast'],
  dateUpdated: '2026-05-14T12:00:00Z',
};

const PROGRESS_WEIGH_INS: [string, number][] = [
  ['2026-04-22T08:00:00Z', 82],
  ['2026-05-13T08:00:00Z', 80.6],
  ['2026-06-09T08:00:00Z', 79],
];

function pinNow() {
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(new Date(2026, 5, 10, 12, 0, 0));
}

function weighIns(entries: [string, number][]): MeasurementDto[] {
  return entries.map(([measuredAt, weightKg], index) => ({
    measurementId: `00000000-0000-0000-1335-0000000001${String(index).padStart(2, '0')}`,
    measuredAt,
    weightKg,
  }));
}

/** An Active plan since 25 May, preceded by a Completed one; both carry a kcal target unless `withKcal` is false. */
function seedPlans(state: ClientDetailState, options: { onlyNewPlan?: boolean; withKcal?: boolean } = {}) {
  const withKcal = options.withKcal ?? true;
  const startDate = options.onlyNewPlan ? '2026-04-13' : '2026-05-25';
  const oldPlan: ClientPlanItem = {
    planId: OLD_PLAN_ID,
    name: 'Stub old plan',
    periodStart: '2026-04-13',
    periodEnd: '2026-05-24',
    status: 'Completed',
    ...(withKcal ? { plannedDailyKcal: 2400 } : {}),
  };
  const newPlan: ClientPlanItem = {
    planId: NEW_PLAN_ID,
    name: 'Stub new plan',
    periodStart: startDate,
    status: 'Active',
    ...(withKcal ? { plannedDailyKcal: 2200 } : {}),
  };
  if (!options.onlyNewPlan) {
    addPlan(state, oldPlan);
  }
  addPlan(state, newPlan, () =>
    nutritionPlan(NEW_PLAN_ID, {
      name: 'Stub new plan',
      globalSettings: { dailyKcal: 2200 },
      weeks: [{ weekNumber: 1, status: WeekStatus.Published, days: [] }],
      startDate,
    }),
  );
}

/** A state with the stub plans, the three weigh-ins and the given onboarding block. */
function progressState(
  onboarding: OnboardingDataDto | undefined = FULL_ONBOARDING,
  entries: [string, number][] = PROGRESS_WEIGH_INS,
): ClientDetailState {
  const state = newClientDetailState({
    dashboard: clientDashboard({ onboarding }),
    measurements: { items: weighIns(entries), totalCount: entries.length, page: 1, pageSize: 50 },
  });
  serveClientDetail(state);
  return state;
}

async function findCard(name: string | RegExp) {
  return screen.findByRole('region', { name });
}

describe('Nutrition tab: Is it working? card', () => {
  it('charts weight against the planned kcal and marks the plan change', async () => {
    pinNow();
    const state = progressState();
    seedPlans(state);
    renderNutritionTab();

    const card = await findCard('Is it working?');
    expect(within(card).getByText('Last 8 weeks', { exact: true })).toBeInTheDocument();

    // 82 kg -> 79 kg over the window, heading for 75 kg at about 0.06 kg a day.
    expect(await within(card).findByTestId('progress-delta')).toHaveTextContent(/^-3 kg$/);
    expect(within(card).getByTestId('progress-status')).toHaveTextContent(/^on track for 75 kg by Aug$/);

    const chart = within(card).getByRole('img');
    expect(chart).toHaveAccessibleName(/Weight change -3 kg over the last 8 weeks.*1 plan changes/);
    expect(within(chart).getAllByTestId('progress-bar')).toHaveLength(8);
    expect(within(chart).getAllByTestId('progress-plan-change')).toHaveLength(1);
    expect(within(chart).getByText('new plan', { exact: true })).toBeInTheDocument();
    expect(within(chart).getByText('W1', { exact: true })).toBeInTheDocument();
    expect(within(chart).getByText('W8', { exact: true })).toBeInTheDocument();
    expect(within(card).getByText('Weight (check-ins)', { exact: true })).toBeInTheDocument();
    expect(within(card).getByText('Planned kcal a day', { exact: true })).toBeInTheDocument();
  });

  it('says there are too few weigh-ins and still draws the planned kcal', async () => {
    pinNow();
    const state = progressState(FULL_ONBOARDING, [['2026-06-09T08:00:00Z', 79]]);
    seedPlans(state, { onlyNewPlan: true });
    renderNutritionTab();

    const card = await findCard('Is it working?');
    expect(await within(card).findByTestId('progress-insufficient')).toHaveTextContent(
      /^Not enough weigh-ins yet to show a change\.$/,
    );
    expect(within(card).queryByTestId('progress-delta')).not.toBeInTheDocument();
    const chart = within(card).getByRole('img');
    expect(within(chart).getAllByTestId('progress-bar')).toHaveLength(8);
    expect(within(chart).queryByTestId('progress-plan-change')).not.toBeInTheDocument();
  });

  it('shows the empty copy when there is no weigh-in and no kcal target', async () => {
    pinNow();
    const state = progressState(FULL_ONBOARDING, []);
    seedPlans(state, { withKcal: false });
    renderNutritionTab();

    const card = await findCard('Is it working?');
    expect(await within(card).findByRole('status')).toHaveTextContent(
      /^Nothing to show yet\. Weigh-ins and a plan with a kcal target will appear here\.$/,
    );
    expect(within(card).queryByRole('img')).not.toBeInTheDocument();
  });

  it('shows a retry when the weigh-ins fail to load and recovers', async () => {
    pinNow();
    const state = progressState();
    seedPlans(state);
    let failing = true;
    server.use(
      http.get(apiUrl(`/trainer/clients/${CLIENT_ID}/measurements`), () =>
        failing
          ? HttpResponse.json({ status: 500 }, { status: 500, headers: { 'Content-Type': 'application/problem+json' } })
          : HttpResponse.json(state.measurements),
      ),
    );
    const { user } = renderNutritionTab();

    const card = await findCard('Is it working?');
    expect(await within(card).findByText("Couldn't load the data. Please refresh the page.")).toBeInTheDocument();
    // The rest of the tab is unaffected.
    expect(await screen.findByRole('region', { name: 'Past plans' })).toBeInTheDocument();

    failing = false;
    await user.click(within(card).getByRole('button', { name: 'Retry' }));
    expect(await within(card).findByTestId('progress-delta')).toHaveTextContent(/^-3 kg$/);
  });
});

describe('Nutrition tab: nutrition profile card', () => {
  const PROFILE = /nutrition profile$/;

  function renderProfile(onboarding: OnboardingDataDto | undefined = FULL_ONBOARDING) {
    pinNow();
    const state = progressState(onboarding);
    seedPlans(state);
    return { state, ...renderNutritionTab() };
  }

  /** Listed chips of a card whose text contains `text` (case-insensitive, like a substring filter). */
  function chips(card: HTMLElement, text: string) {
    return within(card)
      .queryAllByRole('listitem')
      .filter((item) => item.textContent?.toLowerCase().includes(text.toLowerCase()));
  }

  it('shows the onboarding targets, allergies, dislikes and habits with the update date', async () => {
    renderProfile();

    const card = await findCard(PROFILE);
    for (const heading of ['Targets', 'Allergies', 'Dislikes', 'Habits']) {
      expect(within(card).getByRole('heading', { name: heading })).toBeInTheDocument();
    }
    for (const chip of ['2,200 kcal', 'P 150 g', 'C 220 g', 'F 70 g', 'Fib 30 g']) {
      expect(chips(card, chip)).toHaveLength(1);
    }
    // Allergies are red and the "none" marker is not listed.
    for (const allergy of ['Gluten', 'Lactose']) {
      const [item] = chips(card, allergy);
      expect(item.className).toMatch(/text-error/);
    }
    expect(chips(card, 'none')).toHaveLength(0);
    expect(chips(card, 'Mushrooms')).toHaveLength(1);
    expect(chips(card, 'Skips breakfast')).toHaveLength(1);
    expect(within(card).getByTestId('nutrition-profile-source')).toHaveTextContent(/^From onboarding · updated May 14$/);
  });

  it('drops the date from the footer, and the edit action when there is no onboarding', async () => {
    const first = renderProfile({ targetWeightKg: 75, adjustedKcal: 2200 });

    const card = await findCard(PROFILE);
    expect(within(card).getByTestId('nutrition-profile-source')).toHaveTextContent(/^From onboarding$/);
    expect(within(card).getAllByText('None', { exact: true })).toHaveLength(3);

    // A fresh page load: the dashboard now has no onboarding block.
    first.unmount();
    first.state.dashboard = clientDashboard();
    renderNutritionTab();
    const reloaded = await findCard(PROFILE);
    expect(await within(reloaded).findByText('No onboarding answers yet', { exact: true })).toBeInTheDocument();
    expect(within(reloaded).queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument();
  });

  it('edits the profile, refreshes the card and keeps the dialog open on errors', async () => {
    const { state, user } = renderProfile();

    const requests: Record<string, unknown>[] = [];
    server.use(
      http.put(apiUrl(`/trainer/clients/${CLIENT_ID}/nutrition-profile`), async ({ request }) => {
        const saved = (await request.json()) as { dislikes: string[]; habits: string[]; fibreGrams?: number };
        requests.push(saved);
        if (requests.length === 1) {
          return HttpResponse.json(
            { status: 409, errorCode: 'CLIENT_NUTRITION_PROFILE_MISSING' },
            { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
          );
        }
        if (requests.length === 2) {
          return HttpResponse.json({ status: 400 }, { status: 400, headers: { 'Content-Type': 'application/problem+json' } });
        }
        state.dashboard = {
          ...state.dashboard,
          onboarding: { ...state.dashboard.onboarding, dislikes: saved.dislikes, habits: saved.habits, fibreGrams: saved.fibreGrams },
        };
        const response: UpdateClientNutritionProfileResponse = { ...saved, allergies: ['gluten', 'lactose'] };
        return HttpResponse.json(response);
      }),
    );

    const card = await findCard(PROFILE);
    await user.click(within(card).getByRole('button', { name: 'Edit' }));

    const dialog = await screen.findByRole('dialog', { name: 'Edit nutrition profile' });
    const fibre = within(dialog).getByLabelText('Fibre target (g a day)');
    expect(fibre).toHaveValue(30);
    expect(fibre).toBeEnabled();

    // A fibre target outside 0-150 is blocked client-side.
    await user.clear(fibre);
    await user.type(fibre, '151');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent(/^Enter a number from 0 to 150\.$/);
    expect(requests).toHaveLength(0);
    await user.clear(fibre);
    await user.type(fibre, '35');

    await user.type(within(dialog).getByLabelText('Dislikes'), 'Olives{Enter}');
    expect(within(dialog).getByRole('button', { name: 'Remove Olives' })).toBeInTheDocument();

    // 409: the translated message, the dialog stays open with the values.
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(
      await screen.findByText('This client has no nutrition targets or onboarding answers to edit yet.', { exact: true }),
    ).toBeInTheDocument();
    expect(dialog).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Remove Olives' })).toBeInTheDocument();

    // 400 without a code: the fallback message.
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(await screen.findByText("Couldn't save the nutrition profile. Please try again.", { exact: true })).toBeInTheDocument();
    expect(dialog).toBeInTheDocument();

    // Third attempt succeeds: toast, dialog closes, the card refetches the dashboard.
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('Nutrition profile saved.', { exact: true })).toBeInTheDocument();
    await waitFor(() => expect(dialog).not.toBeInTheDocument());
    await waitFor(() => expect(chips(card, 'Olives')).toHaveLength(1));
    expect(chips(card, 'Fib 35 g')).toHaveLength(1);

    expect(requests).toHaveLength(3);
    for (const body of requests) {
      expect(body).toMatchObject({
        allergies: ['gluten', 'lactose'],
        dislikes: ['Mushrooms', 'Olives'],
        habits: ['Skips breakfast'],
        fibreGrams: 35,
      });
    }
  });

  it('saves text typed into a tag box without Enter and still validates it', async () => {
    const { state, user } = renderProfile();

    const requests: { dislikes: string[]; habits: string[] }[] = [];
    server.use(
      http.put(apiUrl(`/trainer/clients/${CLIENT_ID}/nutrition-profile`), async ({ request }) => {
        const saved = (await request.json()) as { dislikes: string[]; habits: string[] };
        requests.push(saved);
        state.dashboard = {
          ...state.dashboard,
          onboarding: { ...state.dashboard.onboarding, dislikes: saved.dislikes, habits: saved.habits },
        };
        const response: UpdateClientNutritionProfileResponse = { ...saved, allergies: ['gluten', 'lactose'] };
        return HttpResponse.json(response);
      }),
    );

    const card = await findCard(PROFILE);
    await user.click(within(card).getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit nutrition profile' });

    // Pending text over the item limit is validated like a chip: inline error, no request, dialog stays open.
    const tooLong = 'x'.repeat(61);
    await user.click(within(dialog).getByLabelText('Habits'));
    await user.paste(tooLong);
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(await within(dialog).findByRole('alert')).toBeInTheDocument();
    expect(requests).toHaveLength(0);
    expect(dialog).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: `Remove ${tooLong}` }));

    // Valid pending text in two boxes, no Enter, plain mouse click on Save.
    await user.type(within(dialog).getByLabelText('Dislikes'), 'Olives');
    await user.type(within(dialog).getByLabelText('Habits'), 'Late dinners');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(dialog).not.toBeInTheDocument());

    expect(requests).toHaveLength(1);
    expect(requests[0].dislikes).toEqual(['Mushrooms', 'Olives']);
    expect(requests[0].habits).toEqual(['Skips breakfast', 'Late dinners']);
  });

  it('disables the fibre target when the client has no targets', async () => {
    const { user } = renderProfile({ targetWeightKg: 75, dislikes: ['Mushrooms'] });

    const card = await findCard(PROFILE);
    await user.click(within(card).getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit nutrition profile' });
    expect(within(dialog).getByLabelText('Fibre target (g a day)')).toBeDisabled();
    expect(within(dialog).getByText('Set calorie and macro targets before adding a fibre target.')).toBeInTheDocument();
  });
});
