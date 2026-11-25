/**
 * Fakes for the return from Stripe Checkout: the subscription read answers from a flag the test flips (and counts the
 * reads), the protected shell mounts the real watcher next to the return page, and the clock helpers move a faked
 * clock in poll-sized steps while letting each answer arrive before the next tick.
 */
import { act } from '@testing-library/react';
import { afterEach, vi } from 'vitest';
import type { GetProfileResponse } from '@/api/generated';
import BillingReturnPage from '@/pages/BillingReturnPage';
import ProtectedRoute from '@/routes/ProtectedRoute';
import { useAuthStore } from '@/stores/auth';
import { useCheckoutActivationStore } from '@/stores/checkoutActivation';
import { useToastStore } from '@/stores/toast';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { freeSubscription, liveSubscription } from '@/test/planPicker';
import { renderWithProviders } from '@/test/render';
import { BillingInterval, SubscriptionStatus } from '@/api/generated';

/** The publicId `renderWithProviders` gives the signed-in test user. */
export const COACH_ID = '00000000-0000-0000-0000-000000000001';
/** The faked "now" of the watch tests. */
export const WATCH_NOW = '2026-11-25T12:00:00Z';
export const WATCH_KEY = 'checkoutActivationWatch';

export const ACTIVATED = 'Your Pro plan is now active.';
export const PENDING = /Your payment went through/;

/** Mirrors the watch phases: 5 s polling to 2 min, 30 s polling to 15 min, then focus refresh only. */
export const POLL_STEP_MS = 5_000;
export const SLOW_STEP_MS = 30_000;
export const FAST_PHASE_MS = 120_000;
export const SLOW_PHASE_MS = 900_000;

const realSetTimeout = globalThis.setTimeout;
const realDelay = (ms: number) => new Promise<void>((resolve) => realSetTimeout(resolve, ms));

export interface SubscriptionMock {
  setPaid: (paid: boolean) => void;
  /** Makes the subscription read answer with this status instead of the plan; 200 restores it. */
  setStatus: (status: number) => void;
  reads: () => number;
}

/** Answers the subscription read from a flag the test flips, counts the reads, and serves the profile read beside it. */
export function serveSubscriptionFlag(initiallyPaid: boolean): SubscriptionMock {
  let paid = initiallyPaid;
  let status = 200;
  let reads = 0;
  server.use(
    http.get(apiUrl('/users/me/subscription'), () => {
      reads += 1;
      if (status !== 200) return HttpResponse.json({ status }, { status, headers: { 'Content-Type': 'application/problem+json' } });
      return HttpResponse.json(
        paid ? liveSubscription('pro', BillingInterval.Monthly, { status: SubscriptionStatus.Active }) : freeSubscription,
      );
    }),
    http.get(apiUrl('/users/me'), () => HttpResponse.json({} satisfies GetProfileResponse)),
  );
  return {
    setPaid: (value) => void (paid = value),
    setStatus: (value) => void (status = value),
    reads: () => reads,
  };
}

/** Fakes the timers and `Date` at `WATCH_NOW`; real time keeps flowing so msw answers and Query timers still run. */
export function useWatchClock() {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  vi.setSystemTime(new Date(WATCH_NOW));
}

/** The watch the app keeps in sessionStorage, or null when none is stored. */
export const storedWatch = () => sessionStorage.getItem(WATCH_KEY);

/** How many toasts with exactly this message were ever shown (a page test mounts no Toaster, so none closes). */
export const toastsShown = (message: string | RegExp) =>
  useToastStore.getState().toasts.filter((toast) => (typeof message === 'string' ? toast.message === message : message.test(toast.message))).length;

type Rendered = ReturnType<typeof renderWithProviders>;

/** Lets every read already started finish, on the real clock. */
async function settle(queryClient: Rendered['queryClient']) {
  for (let attempt = 0; attempt < 400 && queryClient.isFetching() > 0; attempt += 1) {
    await realDelay(5);
  }
  await realDelay(5);
}

/** Advances the faked clock in steps (poll-sized by default), letting each answer arrive before the next tick. */
export async function advanceClock(rendered: Rendered, ms: number, step: number = POLL_STEP_MS) {
  for (let elapsed = 0; elapsed < ms; elapsed += step) {
    await act(async () => {
      await vi.advanceTimersByTimeAsync(step);
      await settle(rendered.queryClient);
    });
  }
}

/** Steps the clock until the activation message has been shown, then returns. */
export async function advanceUntilActivated(rendered: Rendered, step: number) {
  for (let attempt = 0; attempt < 40 && toastsShown(ACTIVATED) === 0; attempt += 1) {
    await advanceClock(rendered, step, step);
  }
}

/** The protected shell the app wraps its pages in (it mounts the watcher), with the return page and a Settings stub. */
export function renderProtectedShell(path: string, roles = ['Trainer']) {
  useToastStore.setState({ toasts: [] });
  return renderWithProviders(
    [
      {
        element: <ProtectedRoute />,
        children: [
          { path: '/settings', element: <p>Settings page</p> },
          { path: '/billing/success', element: <BillingReturnPage /> },
          { path: '/billing/cancel', element: <BillingReturnPage /> },
        ],
      },
      { path: '/login', element: <p>Login page</p> },
    ],
    { initialEntries: [path], auth: { roles } },
  );
}

afterEach(() => {
  vi.useRealTimers();
  useCheckoutActivationStore.getState().stop();
  useToastStore.setState({ toasts: [] });
  useAuthStore.setState({ refreshToken: null });
});
