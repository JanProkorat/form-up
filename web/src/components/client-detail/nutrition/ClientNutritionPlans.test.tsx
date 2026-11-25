/**
 * Client detail, Nutrition tab: the plan list side of the tab. Ported from the Playwright spec
 * (nutritionist/client-detail-nutrition): past plans and "Save as template", the empty state with matched
 * templates and "Use", the current plan's summary line, and Duplicate.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import {
  DietaryStyle,
  LibraryVisibility,
  PrimaryGoal,
  WeekStatus,
} from '@/api/generated';
import type {
  CopyNutritionPlanResponse,
  InstantiateNutritionPlanTemplateResponse,
  NutritionPlanTemplateSummaryDto,
  SearchNutritionPlanTemplatesResponse,
} from '@/api/generated';
import {
  CLIENT_ID,
  addPlan,
  clientDashboard,
  newClientDetailState,
  nutritionPlan,
  renderNutritionTab,
  serveClientDetail,
  servePlan,
} from '@/test/clientDetail';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

afterEach(() => {
  vi.useRealTimers();
});

/** The page's "now", fixed so week and gap maths do not depend on the day the suite runs. */
function pinNow(date: Date) {
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(date);
}

const ACTIVE_PLAN_ID = '00000000-0000-0000-1329-000000000010';
const COMPLETED_PLAN_ID = '00000000-0000-0000-1329-000000000002';
const DRAFT_PLAN_ID = '00000000-0000-0000-1329-000000000001';
const SUMMARY_PLAN_ID = '00000000-0000-0000-1329-000000000003';
const SOURCE_TEMPLATE_ID = '00000000-0000-0000-1329-0000000000c1';
const COPY_PLAN_ID = '00000000-0000-0000-1386-000000000001';

const TEMPLATES = '/nutrition/plan-templates';

/** The Active plan every non-empty test starts from: two published weeks, week 2 under way on 2026-06-10. */
function seedActivePlan(state: ReturnType<typeof newClientDetailState>) {
  addPlan(
    state,
    { planId: ACTIVE_PLAN_ID, name: 'Seeded plan', periodStart: '2026-06-01', status: 'Active', plannedDailyKcal: 2500, weekCount: 2 },
    () =>
      nutritionPlan(ACTIVE_PLAN_ID, {
        name: 'Seeded plan',
        globalSettings: { dailyKcal: 2500 },
        startDate: '2026-06-01',
        weeks: [
          { weekNumber: 1, status: WeekStatus.Published, days: [] },
          { weekNumber: 2, status: WeekStatus.Published, days: [] },
        ],
      }),
  );
}

describe('Nutrition tab: past plans and templates', () => {
  it('lists a completed plan, leaves a gap on the timeline and saves it as a template', async () => {
    pinNow(new Date(2026, 5, 10, 12, 0, 0));
    const state = newClientDetailState();
    serveClientDetail(state);
    seedActivePlan(state);
    addPlan(state, {
      planId: COMPLETED_PLAN_ID,
      name: 'Stub cut — summer',
      periodStart: '2026-01-05',
      periodEnd: '2026-02-01',
      status: 'Completed',
      resultSummary: { compliancePercent: 82, weightDeltaKg: -3.4 },
    });

    const saved: Record<string, unknown>[] = [];
    server.use(
      http.post(apiUrl(`${TEMPLATES}/from-plan`), async ({ request }) => {
        const body = (await request.json()) as Record<string, unknown>;
        saved.push(body);
        const response: NutritionPlanTemplateSummaryDto = {
          templateId: '00000000-0000-0000-1329-0000000000aa',
          name: String(body['name']),
        };
        return HttpResponse.json(response, { status: 201 });
      }),
    );

    const { user } = renderNutritionTab();

    const past = await screen.findByRole('region', { name: 'Past plans' });
    expect(within(past).getByText('Stub cut — summer')).toBeInTheDocument();
    expect(within(past).getByText('82 %')).toBeInTheDocument();
    expect(within(past).getByText(/-3\.4 kg/)).toBeInTheDocument();
    expect(await screen.findByText('No plan', { exact: true })).toBeInTheDocument();

    // Opening, duplicating and saving as a template are all live.
    await user.click(within(past).getByRole('button', { name: /Actions for Stub cut/ }));
    expect(await screen.findByRole('menuitem', { name: 'Open plan' })).not.toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('menuitem', { name: /Duplicate for/ })).not.toHaveAttribute('aria-disabled', 'true');
    await user.click(screen.getByRole('menuitem', { name: 'Save as template' }));

    const dialog = await screen.findByRole('dialog', { name: 'Save as template' });
    const nameInput = within(dialog).getByLabelText('Template name');
    expect(nameInput).toHaveValue('Stub cut — summer');

    // An empty name is blocked client-side.
    await user.clear(nameInput);
    await user.click(within(dialog).getByRole('button', { name: 'Save template' }));
    expect(await within(dialog).findByRole('alert')).toBeInTheDocument();
    expect(saved).toHaveLength(0);

    await user.type(nameInput, 'Summer cut template');
    await user.click(within(dialog).getByRole('button', { name: 'Save template' }));
    expect(await screen.findByText('Template saved to your library.', { exact: true })).toBeInTheDocument();
    await waitFor(() => expect(dialog).not.toBeInTheDocument());
    expect(saved).toHaveLength(1);
    expect(saved[0]).toMatchObject({ planId: COMPLETED_PLAN_ID, name: 'Summer cut template', visibility: LibraryVisibility.Private });
  });

  it('derives the dates and week count of a past plan that has no end date', async () => {
    pinNow(new Date(2026, 5, 10, 12, 0, 0));
    const state = newClientDetailState();
    serveClientDetail(state);
    seedActivePlan(state);
    addPlan(state, {
      planId: COMPLETED_PLAN_ID,
      name: 'Open-ended cut',
      periodStart: '2026-01-05',
      status: 'Completed',
      weekCount: 6,
    });

    renderNutritionTab();

    const past = await screen.findByRole('region', { name: 'Past plans' });
    // Six weeks from Mon 5 Jan end on Sun 15 Feb.
    expect(within(past).getByText('Jan 5 – Feb 15 · 6 weeks')).toBeInTheDocument();
  });

  it('shows the empty state with matched templates and starts a draft from one', async () => {
    // The seeded client's goal is LoseFat: the two LoseFat templates are recommended, the other three are not.
    const state = newClientDetailState({
      dashboard: clientDashboard({ onboarding: { primaryGoal: PrimaryGoal.LoseFat } }),
    });
    serveClientDetail(state);

    const template = (
      templateId: string,
      name: string,
      extra: NutritionPlanTemplateSummaryDto,
    ): NutritionPlanTemplateSummaryDto => ({
      templateId,
      name,
      weekCount: 4,
      usedBy: 0,
      visibility: LibraryVisibility.Private,
      isOwnedByCurrentUser: true,
      version: 1,
      dateCreated: '2026-01-01T00:00:00Z',
      ...extra,
    });
    const allTemplates = [
      template('00000000-0000-0000-1329-0000000000b1', 'Maintenance — 4 weeks', { goal: PrimaryGoal.Maintain, avgKcalPerDay: 2600 }),
      template('00000000-0000-0000-1329-0000000000b2', 'Lean cut — 12 weeks', {
        goal: PrimaryGoal.LoseFat,
        dietaryStyle: DietaryStyle.Standard,
        weekCount: 12,
        avgKcalPerDay: 2200,
        description: 'Moderate deficit, high protein',
      }),
      template('00000000-0000-0000-1329-0000000000b3', 'Vegetarian cut', {
        goal: PrimaryGoal.LoseFat,
        dietaryStyle: DietaryStyle.Vegetarian,
        weekCount: 6,
        avgKcalPerDay: 1950,
      }),
      template('00000000-0000-0000-1329-0000000000b4', 'Bulk — 8 weeks', { goal: PrimaryGoal.GainMuscle, avgKcalPerDay: 3100 }),
      template('00000000-0000-0000-1329-0000000000b5', 'Endurance fuel', { goal: PrimaryGoal.Performance, avgKcalPerDay: 2800 }),
    ];
    // The goal query filters by `goal`; the unfiltered one returns everything.
    server.use(
      http.get(apiUrl(TEMPLATES), ({ request }) => {
        const goal = new URL(request.url).searchParams.get('goal');
        const templates = goal ? allTemplates.filter((entry) => entry.goal === goal) : allTemplates;
        const body: SearchNutritionPlanTemplatesResponse = { templates, totalCount: templates.length, page: 1, pageSize: 100 };
        return HttpResponse.json(body);
      }),
    );
    servePlan(DRAFT_PLAN_ID, () =>
      nutritionPlan(DRAFT_PLAN_ID, {
        name: 'Vegetarian cut',
        status: 'Draft',
        globalSettings: { dailyKcal: 1950 },
        weeks: [1, 2, 3, 4, 5, 6].map((weekNumber) => ({ weekNumber, status: WeekStatus.Draft, days: [] })),
      }),
    );

    const instantiateRequests: Record<string, unknown>[] = [];
    server.use(
      http.post(apiUrl(`${TEMPLATES}/:templateId/instantiate`), async ({ request }) => {
        instantiateRequests.push((await request.json()) as Record<string, unknown>);
        if (instantiateRequests.length === 1) {
          return HttpResponse.json(
            { status: 409, errorCode: 'PLAN_OVERLAP' },
            { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
          );
        }
        state.plans = [{ planId: DRAFT_PLAN_ID, planType: 'Nutrition', name: 'Vegetarian cut', status: 'Draft' }];
        const body: InstantiateNutritionPlanTemplateResponse = { planId: DRAFT_PLAN_ID, name: 'Vegetarian cut', status: 'Draft' };
        return HttpResponse.json(body, { status: 201 });
      }),
    );

    const { user } = renderNutritionTab();

    expect(await screen.findByRole('heading', { name: /has no meal plan yet/ })).toBeInTheDocument();
    expect(screen.queryByText('Preview as client')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Copy from another client' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Start blank' })).toBeEnabled();
    expect(screen.getByRole('link', { name: 'Browse all templates' })).toHaveAttribute('href', '/plan-templates');

    // Every template sits in one scrollable row: the ones fitting the client's goal first, each marked
    // Recommended, then the rest.
    const cards = await screen.findAllByRole('radio');
    expect(cards).toHaveLength(5);
    // jsdom does not load the stylesheet, so the scrolling is checked by its utility class instead of computed CSS.
    expect(screen.getByTestId('template-row')).toHaveClass('overflow-x-auto');
    expect(cards[0]).toHaveTextContent('Lean cut — 12 weeks');
    expect(cards[0]).toHaveTextContent('Recommended');
    expect(cards[0]).toHaveAttribute('aria-checked', 'true');
    const recommendedCount = screen.getAllByTestId('template-recommended').length;
    expect(recommendedCount).toBe(2);
    // The marker only sits on the leading cards, never after an unmarked one.
    cards.forEach((card, index) => {
      expect(within(card).queryAllByTestId('template-recommended')).toHaveLength(index < recommendedCount ? 1 : 0);
    });
    expect(screen.getByRole('button', { name: 'Use “Lean cut — 12 weeks”' })).toBeInTheDocument();

    // Choosing another card retargets the primary action.
    await user.click(cards[1]);
    expect(cards[1]).toHaveAttribute('aria-checked', 'true');
    const useButton = screen.getByRole('button', { name: /^Use “/ });
    expect(useButton).toHaveTextContent(cards[1].querySelector('span.text-subhead')?.textContent ?? 'missing');

    // 409 PLAN_OVERLAP: an error toast, the empty state stays.
    await user.click(useButton);
    expect(
      await screen.findByText('The selected date range overlaps an existing plan for this client.', { exact: true }),
    ).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: /has no meal plan yet/ })).toBeInTheDocument();

    // Second attempt succeeds: the tab refetches and shows the new, unscheduled Draft.
    await user.click(useButton);
    expect(await screen.findByRole('heading', { name: 'Vegetarian cut' })).toBeInTheDocument();
    expect(await screen.findByText(/Not scheduled/)).toBeInTheDocument();
    const weeks = await screen.findByRole('list', { name: /Weeks of the plan/ });
    expect(weeks.querySelectorAll('[data-state="draft"]')).toHaveLength(6);
    expect(screen.queryByRole('heading', { name: /has no meal plan yet/ })).not.toBeInTheDocument();

    // Both attempts sent the template's name and a future Monday as the start date.
    expect(instantiateRequests).toHaveLength(2);
    for (const body of instantiateRequests) {
      expect(body['name']).toBe('Vegetarian cut');
      expect(String(body['clientId'])).toMatch(/^[0-9a-f-]{36}$/);
      const startDate = String(body['startDate']);
      expect(startDate).toMatch(/^\d{4}-\d{2}-\d{2}$/);
      const start = new Date(`${startDate}T00:00:00`);
      expect(start.getDay()).toBe(1);
      expect(start.getTime()).toBeGreaterThan(Date.now());
    }
  });
});

describe('Nutrition tab: current plan summary line', () => {
  /** An Active plan in week 1 of 1 (started Monday 8 June 2026), with or without a source template. */
  function renderSummary(sourceTemplateId: string | undefined) {
    pinNow(new Date(2026, 5, 10, 12, 0, 0));
    const state = newClientDetailState();
    serveClientDetail(state);
    addPlan(
      state,
      { planId: SUMMARY_PLAN_ID, name: 'Stub summary plan', periodStart: '2026-06-08', status: 'Active', sourceTemplateId },
      () =>
        nutritionPlan(SUMMARY_PLAN_ID, {
          name: 'Stub summary plan',
          globalSettings: { dailyKcal: 2100 },
          weeks: [{ weekNumber: 1, status: WeekStatus.Published, days: [] }],
          startDate: '2026-06-08',
          sourceTemplateId,
        }),
    );
    return renderNutritionTab();
  }

  it('shows "made from a template" between the kcal and start segments', async () => {
    renderSummary(SOURCE_TEMPLATE_ID);

    const summary = await screen.findByText(/2,100 kcal a day/);
    expect(summary).toHaveTextContent('Week 1 of 1 · 2,100 kcal a day · made from a template · started');
  });

  it('omits the segment and its separator when the plan has no source template', async () => {
    renderSummary(undefined);

    const summary = await screen.findByText(/2,100 kcal a day/);
    expect(summary).toHaveTextContent('Week 1 of 1 · 2,100 kcal a day · started');
    expect(summary).not.toHaveTextContent('made from a template');
  });
});

describe('Nutrition tab: Duplicate', () => {
  it('posts the copy and opens the editor', async () => {
    pinNow(new Date(2026, 5, 11, 12, 0, 0));
    const state = newClientDetailState();
    serveClientDetail(state);
    seedActivePlan(state);
    addPlan(state, {
      planId: COMPLETED_PLAN_ID,
      name: 'Stub finished plan',
      periodStart: '2026-01-05',
      periodEnd: '2026-02-01',
      status: 'Completed',
    });

    const copyRequests: { url: string; body: Record<string, unknown> }[] = [];
    server.use(
      http.post(apiUrl('/nutrition/plans/:planId/copy'), async ({ request }) => {
        copyRequests.push({ url: request.url, body: (await request.json()) as Record<string, unknown> });
        const body: CopyNutritionPlanResponse = { planId: COPY_PLAN_ID, status: 'Draft' };
        return HttpResponse.json(body, { status: 201 });
      }),
    );

    const { user, router } = renderNutritionTab();

    const past = await screen.findByRole('region', { name: 'Past plans' });
    await user.click(within(past).getByRole('button', { name: /Actions for Stub finished/ }));
    await user.click(await screen.findByRole('menuitem', { name: /Duplicate for/ }));

    await waitFor(() => expect(router.state.location.pathname).toBe(`/clients/${CLIENT_ID}/nutrition/plans/${COPY_PLAN_ID}`));
    expect(copyRequests).toHaveLength(1);
    expect(copyRequests[0].url).toContain(`/nutrition/plans/${COMPLETED_PLAN_ID}/copy`);
    expect(Object.keys(copyRequests[0].body)).toEqual(['clientId']);
  });
});
