/**
 * Offline-sync conflicts on the coach side: the Recent activity card rows and the bell notification with its
 * "See in client activity" link. Ported from the Playwright spec (the live `offlinesyncconflict` SignalR refresh
 * stays in offline-sync-conflict-live.spec.ts). The conflicts endpoint, the timeline and the notification list are
 * faked with msw; "now" is pinned so the relative times are exact.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import { Outlet } from 'react-router-dom';
import { MealKind, OfflineSyncConflictKind } from '@/api/generated';
import type { GetClientOfflineSyncConflictsResponse, OfflineSyncConflictDto } from '@/api/generated';
import type { NotificationDto } from '@/api/notifications';
import type { ClientTimelineResponse } from '@/api/timeline';
import NotificationsPopover from '@/components/notifications/NotificationsPopover';
import {
  CLIENT_ID,
  clientDashboard,
  newClientDetailState,
  renderOverviewTab,
  serveClientDetail,
} from '@/test/clientDetail';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';

const MINUTE_MS = 60 * 1000;
const HOUR_MS = 60 * MINUTE_MS;
const DAY_MS = 24 * HOUR_MS;

const NOW = new Date('2026-06-15T12:00:00Z').getTime();
const iso = (msAgo: number) => new Date(NOW - msAgo).toISOString();
const logDate = (msAgo: number) => iso(msAgo).slice(0, 10);

const CONFLICTS = apiUrl(`/trainer/clients/${CLIENT_ID}/offline-sync-conflicts`);
const TIMELINE = apiUrl(`/trainer/clients/${CLIENT_ID}/timeline`);

beforeEach(() => {
  // Only Date: timers stay real so msw and Testing Library's polling keep working.
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(NOW);
});

afterEach(() => {
  vi.useRealTimers();
});

function serveTimeline() {
  const body: ClientTimelineResponse = {
    canViewNutritionPlans: true,
    canViewTrainingPlans: false,
    items: [
      {
        id: 'timeline-measurement',
        type: 'measurement',
        occurredAt: iso(HOUR_MS),
        title: 'Measurement',
        measurement: { weightKg: 72.4, bodyFatPercentage: 27 },
      },
      {
        id: 'timeline-plan',
        type: 'nutrition_plan_published',
        occurredAt: iso(10 * DAY_MS),
        title: 'Plan',
        planPublished: { planId: 'p1', planName: 'Cut', weekNumber: 7 },
      },
    ],
  };
  server.use(http.get(TIMELINE, () => HttpResponse.json(body)));
}

/** Serves the conflicts list (GET only); `respond` sees the request, so it can tell the unreviewed-only query apart. */
function serveConflicts(respond: (url: URL) => GetClientOfflineSyncConflictsResponse | number) {
  server.use(
    http.get(CONFLICTS, ({ request }) => {
      const result = respond(new URL(request.url));
      return typeof result === 'number'
        ? HttpResponse.json({ title: 'error' }, { status: result })
        : HttpResponse.json(result);
    }),
  );
}

const baseConflict: OfflineSyncConflictDto = {
  planId: 'p1',
  planName: 'Cut',
  operation: 'LogMealEaten',
  kind: OfflineSyncConflictKind.PlanChanged,
  mealKind: MealKind.Lunch,
  mealLogged: true,
  basedOnPlanVersion: 4,
  currentPlanVersion: 6,
};

/** Opens the client's Overview tab as a nutritionist and returns the Recent activity card. */
async function openRecentActivity(roles: string[] = ['Nutritionist']) {
  serveClientDetail(newClientDetailState());
  renderOverviewTab(roles);
  const heading = await screen.findByRole('heading', { name: 'Recent activity' });
  return heading.parentElement!.parentElement!;
}

describe('offline-sync conflicts in Recent activity', () => {
  it('shows both conflict kinds and ranks a late-synced one by detection time', async () => {
    serveTimeline();
    serveConflicts(() => ({
      items: [
        {
          // Eaten five days ago, synced half an hour ago: ranks above the 1 h old measurement.
          ...baseConflict,
          conflictId: 'c-late',
          mealId: 'm1',
          occurredAt: iso(5 * DAY_MS),
          logDate: logDate(5 * DAY_MS),
          detectedAt: iso(30 * MINUTE_MS),
        },
        {
          ...baseConflict,
          conflictId: 'c-removed',
          kind: OfflineSyncConflictKind.MealRemoved,
          mealId: 'm2',
          mealKind: MealKind.AfternoonSnack,
          mealLogged: false,
          occurredAt: iso(3 * HOUR_MS),
          logDate: logDate(3 * HOUR_MS),
          detectedAt: iso(2 * HOUR_MS),
          // Reviewed: it keeps its chronological place instead of pinning to the top.
          reviewedAt: iso(HOUR_MS),
        },
      ],
    }));

    const card = await openRecentActivity();

    const rows = await within(card).findAllByTestId('activity-conflict');
    expect(rows).toHaveLength(2);

    const planChanged = rows.find((row) => row.dataset.kind === 'PlanChanged')!;
    expect(planChanged).toHaveTextContent('Logged offline on an older plan');
    expect(planChanged).toHaveTextContent('Lunch');
    expect(planChanged).toHaveTextContent('plan version 4');
    expect(planChanged).toHaveTextContent('version 6');

    const removed = rows.find((row) => row.dataset.kind === 'MealRemoved')!;
    expect(removed).toHaveTextContent('Eaten offline, meal since removed');
    expect(removed).toHaveTextContent('Not added to the meal log');

    // Order of the whole card: late conflict, measurement (1 h), removed-meal conflict (2 h), plan.
    const items = within(card).getAllByRole('listitem');
    expect(items).toHaveLength(4);
    expect(items[0]).toHaveTextContent('Logged offline on an older plan');
    expect(items[1]).toHaveTextContent('Measurement');
    expect(items[2]).toHaveTextContent('Eaten offline, meal since removed');
    expect(items[3]).toHaveTextContent('Meal plan published');

    // The right-hand time is the detection time, so the list reads in order.
    expect(within(items[0]).getByTestId('activity-conflict-time')).toHaveTextContent('30 minutes ago');
    expect(items[1]).toHaveTextContent('1 hour ago');
    expect(within(items[2]).getByTestId('activity-conflict-time')).toHaveTextContent('2 hours ago');
  });

  it('pins an unreviewed conflict above newer timeline rows and leaves reviewed ones in place', async () => {
    serveTimeline();
    serveConflicts(() => ({
      items: [
        // Detected 5 h ago (older than the 1 h measurement) but unreviewed: pinned first.
        {
          ...baseConflict,
          conflictId: 'c-open',
          occurredAt: iso(6 * HOUR_MS),
          logDate: logDate(6 * HOUR_MS),
          detectedAt: iso(5 * HOUR_MS),
        },
        // Reviewed: ranks by detection time like any row, so 30 min sits above the 1 h measurement.
        {
          ...baseConflict,
          conflictId: 'c-done',
          kind: OfflineSyncConflictKind.MealRemoved,
          mealKind: MealKind.AfternoonSnack,
          occurredAt: iso(HOUR_MS),
          logDate: logDate(HOUR_MS),
          detectedAt: iso(30 * MINUTE_MS),
          reviewedAt: iso(10 * MINUTE_MS),
        },
      ],
    }));

    const card = await openRecentActivity();

    await within(card).findAllByTestId('activity-conflict');
    const items = within(card).getAllByRole('listitem');
    expect(items).toHaveLength(4);
    expect(items[0]).toHaveAttribute('data-reviewed', 'false');
    expect(items[0]).toHaveTextContent('Logged offline on an older plan');
    expect(items[1]).toHaveAttribute('data-reviewed', 'true');
    expect(items[1]).toHaveTextContent('Eaten offline, meal since removed');
    expect(items[2]).toHaveTextContent('Measurement');
  });

  it('pins an old unreviewed conflict that sits behind five newer reviewed ones', async () => {
    serveTimeline();
    const reviewed: OfflineSyncConflictDto[] = [1, 2, 3, 4, 5].map((n) => ({
      ...baseConflict,
      conflictId: `c-done-${n}`,
      kind: OfflineSyncConflictKind.MealRemoved,
      mealKind: MealKind.AfternoonSnack,
      occurredAt: iso(n * MINUTE_MS),
      logDate: logDate(n * MINUTE_MS),
      detectedAt: iso(n * MINUTE_MS),
      reviewedAt: iso(MINUTE_MS / 2),
    }));
    const oldOpen: OfflineSyncConflictDto = {
      ...baseConflict,
      conflictId: 'c-old-open',
      occurredAt: iso(20 * DAY_MS),
      logDate: logDate(20 * DAY_MS),
      detectedAt: iso(20 * DAY_MS),
    };
    // The server orders by detection time regardless of review state, so the newest page holds only reviewed rows.
    serveConflicts((url) => ({ items: url.searchParams.get('unreviewedOnly') === 'true' ? [oldOpen] : reviewed }));

    const card = await openRecentActivity();

    // One pinned row plus the five-row card limit for the rest.
    await waitFor(() => expect(within(card).getAllByRole('listitem')).toHaveLength(6));
    expect(within(card).getAllByTestId('activity-conflict')).toHaveLength(6);
    const items = within(card).getAllByRole('listitem');
    expect(items[0]).toHaveAttribute('data-reviewed', 'false');
    expect(items[0]).toHaveTextContent('Logged offline on an older plan');
    expect(items[1]).toHaveAttribute('data-reviewed', 'true');
  });

  it('a failing conflicts endpoint keeps the timeline rows', async () => {
    serveTimeline();
    serveConflicts(() => 500);

    const card = await openRecentActivity();

    expect(await within(card).findByText('Measurement', { exact: true })).toBeInTheDocument();
    expect(within(card).queryByTestId('activity-conflict')).not.toBeInTheDocument();
    expect(within(card).queryByRole('button', { name: 'Retry' })).not.toBeInTheDocument();
  });

  it('a 403 from the conflicts endpoint reads as an empty list', async () => {
    serveTimeline();
    serveConflicts(() => 403);

    const card = await openRecentActivity();

    expect(await within(card).findByText('Measurement', { exact: true })).toBeInTheDocument();
    expect(within(card).queryByTestId('activity-conflict')).not.toBeInTheDocument();
  });
});

describe('offline-sync conflict notification in the bell', () => {
  const CLIENT_PUBLIC_ID = '33333333-3333-3333-3333-333333333333';
  const NOTIFICATION_ID = '44444444-4444-4444-4444-444444444444';

  /** Mounts the bell next to a stand-in client page; serves one unread conflict notification. */
  function renderBell(actionPayload: string | null) {
    let markedRead = false;
    const readRequests: string[] = [];
    const notification = (): NotificationDto => ({
      id: NOTIFICATION_ID,
      type: 'offlinesyncconflict',
      title: 'Offline entry conflicts with your plan',
      body: 'Jana Novakova logged a meal on an older version of Cut.',
      timestamp: new Date(NOW).toISOString(),
      read: markedRead,
      actionPayload,
    });
    server.use(
      http.get(apiUrl('/client/notifications'), () => HttpResponse.json({ items: [notification()], cursor: null })),
      http.post(apiUrl(`/client/notifications/${NOTIFICATION_ID}/read`), () => {
        markedRead = true;
        readRequests.push(NOTIFICATION_ID);
        return new HttpResponse(null, { status: 204 });
      }),
      http.get(apiUrl('/users/me/subscription'), () => HttpResponse.json({ features: {} })),
    );
    const view = renderWithProviders(
      [
        {
          element: (
            <>
              <NotificationsPopover collapsed={false} rowClassName="" label="Notifications" />
              <Outlet />
            </>
          ),
          children: [
            { path: '/clients', element: null },
            { path: '/clients/:clientId', element: <div data-testid="client-page" /> },
          ],
        },
      ],
      { initialEntries: ['/clients'], auth: { roles: ['Nutritionist'] } },
    );
    return { ...view, readRequests };
  }

  it('shows initials, the server copy and a link to the client that marks it read', async () => {
    const { user, router, readRequests } = renderBell(
      JSON.stringify({ clientName: 'Jana Novakova', planName: 'Cut', count: 1, clientPublicId: CLIENT_PUBLIC_ID }),
    );

    await user.click(await screen.findByRole('button', { name: /^Notifications/ }));
    const popover = await screen.findByRole('dialog', { name: 'Notifications' });
    const item = await within(popover).findByTestId('notification-item');

    expect(within(item).getByTestId('notification-conflict-avatar')).toHaveTextContent('JN');
    expect(item).toHaveTextContent('Offline entry conflicts with your plan');
    expect(item).toHaveTextContent('Jana Novakova logged a meal on an older version of Cut.');

    await user.click(within(item).getByRole('button', { name: /See in client activity/ }));

    await waitFor(() => expect(readRequests).toEqual([NOTIFICATION_ID]));
    await waitFor(() => expect(router.state.location.pathname).toBe(`/clients/${CLIENT_PUBLIC_ID}`));
  });

  it('renders without a link when the payload is not usable', async () => {
    const { user } = renderBell('not json');

    await user.click(await screen.findByRole('button', { name: /^Notifications/ }));
    const popover = await screen.findByRole('dialog', { name: 'Notifications' });
    const item = await within(popover).findByTestId('notification-item');

    expect(item).toHaveTextContent('Offline entry conflicts with your plan');
    expect(within(item).queryByRole('button', { name: /See in client activity/ })).not.toBeInTheDocument();
  });
});

describe('trainer-only viewer', () => {
  it('never calls the conflicts endpoint', async () => {
    const conflictRequests: string[] = [];
    server.use(
      http.get(CONFLICTS, ({ request }) => {
        conflictRequests.push(request.url);
        return HttpResponse.json({ items: [] });
      }),
    );
    serveTimeline();
    serveClientDetail(
      newClientDetailState({
        dashboard: clientDashboard({ canViewNutritionPlans: false, canViewTrainingPlans: true }),
      }),
    );
    renderOverviewTab(['Trainer']);

    const heading = await screen.findByRole('heading', { name: 'Recent activity' });
    const card = heading.parentElement!.parentElement!;
    // The card has settled once the timeline's own rows (or its empty state) are showing.
    await within(card).findByText('Measurement', { exact: true });

    expect(conflictRequests).toEqual([]);
    expect(within(card).queryByTestId('activity-conflict')).not.toBeInTheDocument();
  });
});
