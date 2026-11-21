/**
 * Shared harness for the Check-ins page tests: renders the real CheckInsPage on `/check-ins` and serves the
 * requests it makes from one mutable `CheckInsState`. Payloads are typed with the generated API types.
 */
import { Outlet } from 'react-router-dom';
import type { RouteObject } from 'react-router-dom';
import type {
  GetMySubscriptionResponse,
  GetTrainerCheckInsResponse,
  MarkCheckInReviewedResponse,
  TrainerCheckInDto,
} from '@/api/generated';
import { Toaster } from '@/components/ui/toast';
import CheckInsPage from '@/pages/CheckInsPage';
import { useToastStore } from '@/stores/toast';
import { HttpResponse, apiUrl, http, problem } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';

export interface CheckInsState {
  rows: TrainerCheckInDto[];
  /** Status to answer GET /trainer/weekly-check-ins with instead of the rows. */
  listStatus?: number;
  /** Status to answer mark-reviewed with; omitted means success and the row turns Reviewed. */
  markStatus?: number;
  /** When set, mark-reviewed answers only after this promise resolves, so a test can inspect the in-flight state. */
  markGate?: Promise<void>;
  /** Plan features; `canUseWeeklyCheckIns: false` locks the page. */
  features: NonNullable<GetMySubscriptionResponse['features']>;
  /** The `weekStartDate` of every list request, in order. */
  listWeeks: (string | null)[];
  /** The id of every mark-reviewed request, in order. */
  marked: string[];
}

let nextRow = 1;

/** One answered, unreviewed check-in of a client in Prague. */
export function checkIn(overrides: Partial<TrainerCheckInDto> = {}): TrainerCheckInDto {
  const n = nextRow++;
  return {
    id: `aaaaaaaa-0000-0000-0000-${String(n).padStart(12, '0')}`,
    clientUserId: `22222222-0000-0000-0000-${String(n).padStart(12, '0')}`,
    clientPublicId: `11111111-0000-0000-0000-${String(n).padStart(12, '0')}`,
    clientName: `Client ${n}`,
    profession: 'Training',
    weekStartDate: '2026-10-12',
    flags: [],
    sentAt: '2026-10-16T16:00:00Z',
    respondedAt: '2026-10-19T17:40:00Z',
    status: 'Responded',
    dueAt: '2026-10-21T16:00:00Z',
    formId: 'ffffffff-0000-0000-0000-000000000001',
    formName: 'Weekly check-in',
    clientTimeZone: 'Europe/Prague',
    clientIsOutsidePlan: false,
    ...overrides,
  };
}

export function newCheckInsState(overrides: Partial<CheckInsState> = {}): CheckInsState {
  return { rows: [], features: {}, listWeeks: [], marked: [], ...overrides };
}

/** Serves the page's own requests from `state`. */
export function serveCheckIns(state: CheckInsState): void {
  server.use(
    http.get(apiUrl('/trainer/weekly-check-ins'), ({ request }) => {
      state.listWeeks.push(new URL(request.url).searchParams.get('weekStartDate'));
      if (state.listStatus) {
        return problem(state.listStatus, 'SERVER_ERROR');
      }
      const body: GetTrainerCheckInsResponse = { checkIns: state.rows };
      return HttpResponse.json(body);
    }),
    http.post(apiUrl('/trainer/weekly-check-ins/:id/mark-reviewed'), async ({ params }) => {
      const id = String(params['id']);
      state.marked.push(id);
      await state.markGate;
      if (state.markStatus) {
        return problem(state.markStatus, 'CHECK_IN_NOT_FOUND');
      }
      const reviewedAt = '2026-10-20T08:00:00Z';
      state.rows = state.rows.map((row) =>
        row.id === id ? { ...row, status: 'Reviewed', reviewedByTrainerAt: reviewedAt } : row,
      );
      const body: MarkCheckInReviewedResponse = { id, reviewedAt };
      return HttpResponse.json(body);
    }),
    http.get(apiUrl('/users/me/subscription'), () => HttpResponse.json({ features: state.features })),
  );
}

/** Mounts CheckInsPage on `/check-ins` with the toast outlet the app shell provides. */
export function renderCheckInsPage() {
  useToastStore.setState({ toasts: [] });
  const routes: RouteObject[] = [
    {
      element: (
        <>
          <Outlet />
          <Toaster />
        </>
      ),
      children: [
        { path: '/check-ins', element: <CheckInsPage /> },
        // Link targets are not part of this page; a test reads `router.state.location` to see where it led.
        { path: '/clients/:clientId', element: <div data-testid="client-page" /> },
        { path: '/clients/:clientId/forms', element: <div data-testid="forms-tab" /> },
        { path: '/inbox', element: <div data-testid="inbox-page" /> },
      ],
    },
  ];
  return renderWithProviders(routes, { initialEntries: ['/check-ins'], auth: { roles: ['Trainer'] } });
}
