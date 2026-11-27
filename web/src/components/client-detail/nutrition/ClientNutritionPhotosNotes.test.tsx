/**
 * Client detail, Nutrition tab: the "Meal photos and notes" card. Ported from the Playwright spec
 * (nutritionist/client-detail-nutrition). "Now" is pinned to Thursday 11 June 2026, noon, so the week runs
 * Mon 8 June to Sun 14 June.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, within } from '@testing-library/react';
import { MealKind, PlanPhotoCategory, PlanPhotoType, WeekStatus } from '@/api/generated';
import type {
  CheckInHistoryWeekDto,
  ClientPhotoResponse,
  GetClientCheckInHistoryResponse,
  GetTrainerClientPhotosResponse,
} from '@/api/generated';
import type { CheckInDetailDto, GetTrainerCheckInsResponse, TrainerCheckInDto } from '@/api/weekly-checkins';
import {
  CLIENT_ID,
  CLIENT_USER_ID,
  COACH_USER_ID,
  addPlan,
  newClientDetailState,
  nutritionPlan,
  renderNutritionTab,
  serveClientDetail,
} from '@/test/clientDetail';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

afterEach(() => {
  vi.useRealTimers();
});

const PHOTO_HOST = 'https://photos.stub.test';
const OTHER_CLIENT_USER_ID = '99999999-9999-9999-9999-999999999999';
const NUTRITION_CHECK_IN_ID = '00000000-0000-0000-1336-0000000000a1';
const TRAINING_CHECK_IN_ID = '00000000-0000-0000-1336-0000000000a2';
const OTHER_CLIENT_CHECK_IN_ID = '00000000-0000-0000-1336-0000000000a3';
const PLAN_ID = '00000000-0000-0000-1336-000000000001';

const NOW = new Date(2026, 5, 11, 12, 0, 0);
const MONDAY = new Date(2026, 5, 8);
const NEXT_MONDAY = new Date(2026, 5, 15);

function pinNow() {
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(NOW);
}

function atLocalNoon(day: number): string {
  return new Date(2026, 5, day, 12, 0, 0).toISOString();
}

function weekday(day: number): string {
  return new Intl.DateTimeFormat('en', { weekday: 'short' }).format(new Date(2026, 5, day, 12, 0, 0));
}

function checkInDate(day: number): string {
  return new Intl.DateTimeFormat('en', { weekday: 'short', day: 'numeric', month: 'short' }).format(
    new Date(2026, 5, day, 12, 0, 0),
  );
}

function photo(id: string, takenAtDay: number, mealKind: MealKind | undefined, displayUrl = `${PHOTO_HOST}/ok-${id}.png`): ClientPhotoResponse {
  return {
    id: `00000000-0000-0000-1336-0000000001${id}`,
    blobUrl: `client-photos/${id}`,
    displayUrl,
    category: PlanPhotoCategory.Food,
    planType: PlanPhotoType.Nutrition,
    mealKind,
    takenAt: atLocalNoon(takenAtDay),
  };
}

const MEAL_PHOTOS: ClientPhotoResponse[] = [
  photo('01', 8, MealKind.Breakfast),
  photo('02', 9, MealKind.MorningSnack),
  photo('03', 10, MealKind.Dinner),
  photo('04', 11, undefined),
  photo('05', 10, MealKind.Lunch, `${PHOTO_HOST}/expired.png`),
];

interface PhotosControl {
  failing: boolean;
  requests: URL[];
}

function servePhotos(photos: ClientPhotoResponse[], control: PhotosControl = { failing: false, requests: [] }) {
  server.use(
    http.get(apiUrl(`/trainer/clients/${CLIENT_ID}/photos`), ({ request }) => {
      control.requests.push(new URL(request.url));
      if (control.failing) {
        return HttpResponse.json({ status: 500 }, { status: 500, headers: { 'Content-Type': 'application/problem+json' } });
      }
      const body: GetTrainerClientPhotosResponse = { photos };
      return HttpResponse.json(body);
    }),
  );
  return control;
}

interface CheckInStub {
  weeks: CheckInHistoryWeekDto[];
  detail: Partial<CheckInDetailDto>;
  control: { failing: boolean };
  calls: { list: string[]; detail: string[] };
}

function checkInStub(overrides: Partial<CheckInStub> = {}): CheckInStub {
  return {
    weeks: [
      { weekStartDate: '2026-05-25', status: 'answered', hungerScore: 2 },
      { weekStartDate: '2026-06-01', status: 'answered', hungerScore: 4 },
      { weekStartDate: '2026-06-08', status: 'waiting' },
    ],
    detail: { note: 'Felt hungry in the evenings', hungerScore: 4 },
    control: { failing: false },
    calls: { list: [], detail: [] },
    ...overrides,
  };
}

/**
 * Serves the three check-in calls. The list rows carry the client's user id, so the card has to pick the
 * right rows out of a list that also holds another client's.
 */
function serveCheckIns(stub: CheckInStub) {
  server.use(
    http.get(apiUrl(`/trainer/clients/${CLIENT_USER_ID}/weekly-check-ins/history`), () => {
      if (stub.control.failing) {
        return HttpResponse.json({ status: 500 }, { status: 500, headers: { 'Content-Type': 'application/problem+json' } });
      }
      const body: GetClientCheckInHistoryResponse = { weeks: stub.weeks, answeredCount: 2, totalCount: 2 };
      return HttpResponse.json(body);
    }),
    http.get(apiUrl('/trainer/weekly-check-ins'), ({ request }) => {
      stub.calls.list.push(new URL(request.url).searchParams.get('weekStartDate') ?? '');
      const row = (id: string, userId: string, profession: TrainerCheckInDto['profession'], respondedAt: string | undefined): TrainerCheckInDto => ({
        id,
        clientUserId: userId,
        clientPublicId: userId,
        clientName: 'Stub client',
        profession,
        weekStartDate: '2026-06-01',
        flags: [],
        note: undefined,
        sentAt: atLocalNoon(5),
        respondedAt,
        dismissedByClientAt: undefined,
        reviewedByTrainerAt: atLocalNoon(9),
        status: respondedAt ? 'Reviewed' : 'Pending',
        dueAt: undefined,
      });
      const body: GetTrainerCheckInsResponse = {
        checkIns: [
          row(OTHER_CLIENT_CHECK_IN_ID, OTHER_CLIENT_USER_ID, 'Nutrition', atLocalNoon(8)),
          // A newer Training answer must not win over the Nutrition one.
          row(TRAINING_CHECK_IN_ID, CLIENT_USER_ID, 'Training', atLocalNoon(8)),
          row(NUTRITION_CHECK_IN_ID, CLIENT_USER_ID, 'Nutrition', atLocalNoon(7)),
        ],
      };
      return HttpResponse.json(body);
    }),
    http.get(apiUrl('/trainer/weekly-check-ins/:id'), ({ params }) => {
      const id = String(params['id']);
      stub.calls.detail.push(id);
      const body: CheckInDetailDto = {
        id,
        clientUserId: CLIENT_USER_ID,
        clientName: 'Stub client',
        professionalUserId: COACH_USER_ID,
        profession: 'Nutrition',
        weekStartDate: '2026-06-01',
        flags: [],
        note: null,
        sentAt: atLocalNoon(5),
        respondedAt: atLocalNoon(7),
        dismissedByClientAt: null,
        reviewedByTrainerAt: null,
        status: 'Responded',
        dueAt: null,
        hungerScore: null,
        ...stub.detail,
      };
      return HttpResponse.json(body);
    }),
  );
}

/** An Active plan so the tab shows its cards, then the card under test. */
function seedTab() {
  pinNow();
  const state = newClientDetailState();
  serveClientDetail(state);
  addPlan(state, { planId: PLAN_ID, name: 'Stub plan', periodStart: '2026-06-08', status: 'Active' }, () =>
    nutritionPlan(PLAN_ID, {
      name: 'Stub plan',
      weeks: [{ weekNumber: 1, status: WeekStatus.Published, days: [] }],
      startDate: '2026-06-08',
    }),
  );
  return state;
}

async function findCard() {
  return screen.findByRole('region', { name: 'Meal photos and notes' });
}

describe('Nutrition tab: Meal photos and notes card', () => {
  it('captions one photo per meal group, asks for this week only and shows the note', async () => {
    seedTab();
    const photos = servePhotos(MEAL_PHOTOS);
    const stub = checkInStub();
    serveCheckIns(stub);
    renderNutritionTab();

    const card = await findCard();
    expect(within(card).getByText('This week', { exact: true })).toBeInTheDocument();

    const tiles = await within(card).findAllByTestId('meal-photo');
    expect(tiles).toHaveLength(5);
    // Breakfast, Snack (MorningSnack) and Dinner each use the "this week" row labels; a null meal kind is the weekday alone.
    expect(tiles[0]).toHaveTextContent(new RegExp(`^Breakfast · ${weekday(8)}$`));
    expect(tiles[1]).toHaveTextContent(new RegExp(`^Snack · ${weekday(9)}$`));
    expect(tiles[2]).toHaveTextContent(new RegExp(`^Dinner · ${weekday(10)}$`));
    expect(tiles[3]).toHaveTextContent(new RegExp(`^${weekday(11)}$`));
    expect(tiles[3]).not.toHaveTextContent('·');

    // Pre-signed URLs render; the expired one falls back to a placeholder that keeps its caption.
    // jsdom loads no images, so the browser's failed load of the expired URL is raised by hand.
    expect(within(tiles[0]).getByRole('img')).toHaveAttribute('src', `${PHOTO_HOST}/ok-01.png`);
    fireEvent.error(within(tiles[4]).getByRole('img'));
    expect(tiles[4]).toHaveTextContent(new RegExp(`^Lunch · ${weekday(10)}$`));
    expect(within(tiles[4]).getByTestId('meal-photo-unavailable')).toBeInTheDocument();
    expect(within(card).getAllByTestId('meal-photo-unavailable')).toHaveLength(1);

    // Only Food photos from Monday 00:00 to the last millisecond of Sunday, local time.
    expect(photos.requests.length).toBeGreaterThan(0);
    const params = photos.requests[0].searchParams;
    expect(params.get('category')).toBe('Food');
    expect(params.get('pageSize')).toBe('6');
    expect(params.get('from')).toBe(MONDAY.toISOString());
    expect(params.get('to')).toBe(new Date(NEXT_MONDAY.getTime() - 1).toISOString());

    // The note comes from the newest answered week, the client's own Nutrition row.
    const note = within(card).getByTestId('check-in-note');
    expect(await within(note).findByText(/“Felt hungry in the evenings”/)).toBeInTheDocument();
    expect(within(note).getByTestId('check-in-meta')).toHaveTextContent(new RegExp(`^Check-in · ${checkInDate(7)} · hunger 4/5$`));
    expect(stub.calls.list).toEqual(['2026-06-01']);
    expect(stub.calls.detail).toEqual([NUTRITION_CHECK_IN_ID]);
  });

  it('says there is no note and drops the hunger part when the check-in has neither', async () => {
    seedTab();
    servePhotos(MEAL_PHOTOS);
    serveCheckIns(checkInStub({ detail: { note: null, hungerScore: null } }));
    renderNutritionTab();

    const card = await findCard();
    const note = within(card).getByTestId('check-in-note');
    expect(await within(note).findByText('No note in the latest check-in')).toBeInTheDocument();
    expect(within(note).getByTestId('check-in-meta')).toHaveTextContent(new RegExp(`^Check-in · ${checkInDate(7)}$`));
    expect(note.querySelector('blockquote')).toBeNull();
  });

  it('says no check-in was answered yet without asking for a list or a detail', async () => {
    seedTab();
    servePhotos(MEAL_PHOTOS);
    const stub = checkInStub({
      weeks: [
        { weekStartDate: '2026-06-01', status: 'missed' },
        { weekStartDate: '2026-06-08', status: 'waiting' },
      ],
    });
    serveCheckIns(stub);
    renderNutritionTab();

    const card = await findCard();
    expect(await within(card).findByText('No check-in answered yet')).toBeInTheDocument();
    expect(await within(card).findAllByTestId('meal-photo')).toHaveLength(5);
    expect(stub.calls.list).toHaveLength(0);
    expect(stub.calls.detail).toHaveLength(0);
  });

  it('shows the empty copy when there are no photos this week and still shows the note', async () => {
    seedTab();
    servePhotos([]);
    serveCheckIns(checkInStub());
    renderNutritionTab();

    const card = await findCard();
    expect(await within(card).findByText('No meal photos this week.')).toBeInTheDocument();
    expect(within(card).queryAllByTestId('meal-photo')).toHaveLength(0);
    expect(await within(card).findByText(/Felt hungry in the evenings/)).toBeInTheDocument();
  });

  it('shows a retry when the photos fail to load, keeps the rest of the tab and recovers', async () => {
    seedTab();
    const photos = servePhotos(MEAL_PHOTOS, { failing: true, requests: [] });
    serveCheckIns(checkInStub());
    const { user } = renderNutritionTab();

    const card = await findCard();
    expect(await within(card).findByText("Couldn't load the meal photos.")).toBeInTheDocument();
    // The note and the other cards still render.
    expect(await within(card).findByText(/Felt hungry in the evenings/)).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Past plans' })).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Is it working?' })).toBeInTheDocument();

    photos.failing = false;
    await user.click(within(card).getByRole('button', { name: 'Retry' }));
    expect(await within(card).findAllByTestId('meal-photo')).toHaveLength(5);
  });

  it('shows a retry inside the note block when a check-in call fails and keeps the photos', async () => {
    seedTab();
    servePhotos(MEAL_PHOTOS);
    const stub = checkInStub({ control: { failing: true } });
    serveCheckIns(stub);
    const { user } = renderNutritionTab();

    const card = await findCard();
    const note = within(card).getByTestId('check-in-note');
    expect(await within(note).findByText("Couldn't load the latest check-in.")).toBeInTheDocument();
    expect(await within(card).findAllByTestId('meal-photo')).toHaveLength(5);
    expect(within(card).queryByText("Couldn't load the meal photos.")).not.toBeInTheDocument();

    stub.control.failing = false;
    await user.click(within(note).getByRole('button', { name: 'Retry' }));
    expect(await within(note).findByText(/Felt hungry in the evenings/)).toBeInTheDocument();
  });
});
