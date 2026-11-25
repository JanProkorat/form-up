/**
 * The checkout watch: after the return page gave up, the app keeps re-reading the subscription (every 5 s, then every
 * 30 s, then not at all after 15 minutes) and announces the plan once it lands. The faked clock moves in poll-sized
 * steps; the reload case (the watch surviving in sessionStorage) is an E2E test.
 */
import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { useAuthStore } from '@/stores/auth';
import { useCheckoutActivationStore } from '@/stores/checkoutActivation';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import {
  ACTIVATED,
  COACH_ID,
  FAST_PHASE_MS,
  POLL_STEP_MS,
  SLOW_PHASE_MS,
  SLOW_STEP_MS,
  advanceClock,
  advanceUntilActivated,
  renderProtectedShell,
  serveSubscriptionFlag,
  storedWatch,
  toastsShown,
  useWatchClock,
} from '@/test/checkoutReturn';

/** A watch already running, as the return page leaves it, with the shell and the watcher mounted. */
function startWatching(initiallyPaid = false) {
  useWatchClock();
  const subscription = serveSubscriptionFlag(initiallyPaid);
  useCheckoutActivationStore.getState().start(COACH_ID);
  const rendered = renderProtectedShell('/settings');
  return { subscription, rendered };
}

describe('CheckoutActivationWatcher', () => {
  it('announces the plan once when it lands, then stops reading', async () => {
    const { subscription, rendered } = startWatching();
    expect(await screen.findByText('Settings page')).toBeInTheDocument();
    await advanceClock(rendered, 2 * POLL_STEP_MS);
    expect(toastsShown(ACTIVATED)).toBe(0);

    subscription.setPaid(true);
    await advanceUntilActivated(rendered, POLL_STEP_MS);

    // The watch ends with the announcement: no further reads on the polling cadence.
    const readsAfterLanding = subscription.reads();
    await advanceClock(rendered, FAST_PHASE_MS / 2);
    expect(subscription.reads()).toBe(readsAfterLanding);
    expect(storedWatch()).toBeNull();
    expect(toastsShown(ACTIVATED)).toBe(1);
  });

  it('polls more slowly after two minutes and still announces the plan when it lands', async () => {
    const { subscription, rendered } = startWatching();
    await screen.findByText('Settings page');
    await advanceClock(rendered, FAST_PHASE_MS, SLOW_STEP_MS);

    // Polling is slower now: two minutes of clock make a handful of reads, not the 24 a 5 s cadence would.
    const readsBefore = subscription.reads();
    await advanceClock(rendered, FAST_PHASE_MS, SLOW_STEP_MS);
    expect(subscription.reads() - readsBefore).toBeLessThanOrEqual(5);

    subscription.setPaid(true);
    await advanceUntilActivated(rendered, SLOW_STEP_MS);
    expect(storedWatch()).toBeNull();
    expect(toastsShown(ACTIVATED)).toBe(1);
  });

  it('stops polling after 15 minutes when the plan never lands', async () => {
    const { subscription, rendered } = startWatching();
    await screen.findByText('Settings page');

    await advanceClock(rendered, SLOW_PHASE_MS + SLOW_STEP_MS, SLOW_STEP_MS);
    const readsAtBound = subscription.reads();
    // The watch did poll all that time (2 minutes at 5 s, then 13 minutes at 30 s), so the stop is not an idle watch.
    expect(readsAtBound).toBeGreaterThan(30);
    await advanceClock(rendered, FAST_PHASE_MS, SLOW_STEP_MS);

    expect(subscription.reads()).toBe(readsAtBound);
    expect(toastsShown(ACTIVATED)).toBe(0);
  });

  it('does not poll for a watch that belongs to another coach', async () => {
    useWatchClock();
    const subscription = serveSubscriptionFlag(false);
    useCheckoutActivationStore.setState({
      startedAt: Date.now() - 1_000,
      userId: 'another-coach',
      targetPlanCode: null,
      awaitsSecondRoleAddOn: false,
    });

    const rendered = renderProtectedShell('/settings');
    await screen.findByText('Settings page');

    // The foreign watch is dropped once the signed-in coach is known, and nothing polls on its behalf.
    expect(useCheckoutActivationStore.getState().startedAt).toBeNull();
    await advanceClock(rendered, FAST_PHASE_MS / 2);
    expect(subscription.reads()).toBe(0);
    expect(toastsShown(ACTIVATED)).toBe(0);
  });

  it('keeps watching through a failing subscription read', async () => {
    const { subscription, rendered } = startWatching();
    await screen.findByText('Settings page');

    subscription.setStatus(500);
    await advanceClock(rendered, FAST_PHASE_MS / 2);
    expect(toastsShown(ACTIVATED)).toBe(0);
    expect(useCheckoutActivationStore.getState().startedAt).not.toBeNull();
    expect(storedWatch()).not.toBeNull();

    subscription.setStatus(200);
    subscription.setPaid(true);
    await advanceUntilActivated(rendered, POLL_STEP_MS);
    expect(toastsShown(ACTIVATED)).toBe(1);
  });

  it('clears the watch when the session cannot be refreshed', async () => {
    const { subscription, rendered } = startWatching();
    await screen.findByText('Settings page');
    expect(storedWatch()).not.toBeNull();

    useAuthStore.setState({ refreshToken: 'test-refresh-token' });
    server.use(http.post(apiUrl('/auth/refresh'), () => HttpResponse.json({ status: 401 }, { status: 401 })));
    subscription.setStatus(401);
    await advanceClock(rendered, POLL_STEP_MS);

    expect(await screen.findByText('Login page')).toBeInTheDocument();
    expect(rendered.router.state.location.pathname).toBe('/login');
    expect(storedWatch()).toBeNull();
    expect(useCheckoutActivationStore.getState().startedAt).toBeNull();
  });
});
