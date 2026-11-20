/**
 * Shared harness for the admin coach profile page tests: a payload builder typed with the generated response type,
 * msw handlers for the page's loads (the coach and the plan catalogue), and a render of the real
 * AdminCoachProfilePage inside a fake realtime context that captures the handlers the page registers.
 */
import {
  AdminCoachStatus,
  BillingInterval,
  type GetAdminCoachResponse,
  type SubscriptionPlanDto,
} from '@/api/generated';
import { RealtimeContext, type RealtimeApi } from '@/hooks/useSignalR';
import AdminCoachProfilePage from '@/pages/AdminCoachProfilePage';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';

export const COACH_ID = '11111111-1111-1111-1111-111111111111';

/** A paying coach with a Stripe customer and an empty history. */
export function adminCoachPayload(overrides: Partial<GetAdminCoachResponse> = {}): GetAdminCoachResponse {
  return {
    coachId: COACH_ID,
    name: 'Test Coach',
    email: 'coach@example.com',
    roles: ['Trainer'],
    joinedAt: '2026-01-10T09:00:00Z',
    plan: { code: 'pro', nameCs: 'Pro', nameEn: 'Pro', nameDe: 'Pro' },
    status: AdminCoachStatus.Paying,
    priceMinorUnits: 64900,
    currency: 'CZK',
    billingInterval: BillingInterval.Monthly,
    currentPeriodEndsAt: '2026-12-10T09:00:00Z',
    cancelAtPeriodEnd: false,
    clientsUsed: 4,
    clientsMax: 30,
    canEndPlanNow: true,
    hasCancellableSubscription: true,
    freePlanMaxActiveClients: 3,
    stripeCustomerId: 'cus_Abc123',
    history: [],
    ...overrides,
  };
}

const proPlan: SubscriptionPlanDto = {
  code: 'pro',
  nameCs: 'Pro',
  nameEn: 'Pro',
  nameDe: 'Pro',
  bothRolesIncluded: false,
  priceMinorUnits: 64900,
  currency: 'CZK',
  billingInterval: BillingInterval.Monthly,
  maxActiveClients: 30,
  isActive: true,
  coachCount: 1,
};

export interface AdminCoachState {
  /** How many times the coach was fetched. */
  coachRequests: number;
  /** Sets the next coach payload; the page shows it after its next fetch. */
  setCoach: (coach: GetAdminCoachResponse) => void;
}

/** Serves the coach and the plan catalogue; the coach payload can change between fetches. */
export function serveAdminCoach(initial: GetAdminCoachResponse = adminCoachPayload()): AdminCoachState {
  let current = initial;
  const state: AdminCoachState = {
    coachRequests: 0,
    setCoach: (coach) => {
      current = coach;
    },
  };
  server.use(
    http.get(apiUrl(`/admin/coaches/${COACH_ID}`), () => {
      state.coachRequests += 1;
      return HttpResponse.json(current);
    }),
    http.get(apiUrl('/admin/subscription-plans'), () => HttpResponse.json({ plans: [proPlan] })),
  );
  return state;
}

/** A realtime context whose registered handlers a test can fire. */
export interface FakeRealtime {
  api: RealtimeApi;
  /** Fires every handler registered for `event` (any casing) with `payload`. */
  emit: (event: string, payload: unknown) => void;
  /** Fires every reconnect callback. */
  reconnect: () => void;
}

export function createFakeRealtime(): FakeRealtime {
  const handlers = new Map<string, Set<(payload: unknown) => void>>();
  const reconnected = new Set<() => void>();
  return {
    api: {
      subscribe: (event, handler) => {
        const key = event.toLowerCase();
        const set = handlers.get(key) ?? new Set();
        set.add(handler);
        handlers.set(key, set);
        return () => set.delete(handler);
      },
      subscribeReconnected: (callback) => {
        reconnected.add(callback);
        return () => reconnected.delete(callback);
      },
    },
    emit: (event, payload) => handlers.get(event.toLowerCase())?.forEach((handler) => handler(payload)),
    reconnect: () => reconnected.forEach((callback) => callback()),
  };
}

/** Mounts the profile page on `/admin/coach-accounts/:coachId` as an admin, with a fake realtime connection. */
export function renderAdminCoach(language: 'cs' | 'en' | 'de' = 'en') {
  const realtime = createFakeRealtime();
  const rendered = renderWithProviders(
    [
      {
        path: '/admin/coach-accounts/:coachId',
        element: (
          <RealtimeContext.Provider value={realtime.api}>
            <AdminCoachProfilePage />
          </RealtimeContext.Provider>
        ),
      },
    ],
    { initialEntries: [`/admin/coach-accounts/${COACH_ID}`], language, auth: { roles: ['Admin'] } },
  );
  return { ...rendered, realtime };
}
