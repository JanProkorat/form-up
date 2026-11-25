/**
 * '/billing/success' — the page Stripe sends the coach back to: it waits up to 10 s for the webhook, then continues
 * to Plan and billing; when the plan has not landed by then, the pending note is shown once and the watch that keeps
 * looking (see CheckoutActivationWatcher.test.tsx) is started for this coach.
 */
import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { useCheckoutActivationStore } from '@/stores/checkoutActivation';
import {
  ACTIVATED,
  COACH_ID,
  PENDING,
  SLOW_PHASE_MS,
  SLOW_STEP_MS,
  advanceClock,
  renderProtectedShell,
  serveSubscriptionFlag,
  storedWatch,
  toastsShown,
  useWatchClock,
} from '@/test/checkoutReturn';

describe('BillingReturnPage after Checkout', () => {
  it('a plan that is already paid continues without a note', async () => {
    useWatchClock();
    serveSubscriptionFlag(true);

    const { router } = renderProtectedShell('/billing/success');

    expect(await screen.findByText('Settings page')).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/settings');
    expect(router.state.location.hash).toBe('#plan-billing');
    expect(toastsShown(PENDING)).toBe(0);
    expect(toastsShown(ACTIVATED)).toBe(0);
    expect(useCheckoutActivationStore.getState().startedAt).toBeNull();
  });

  it('gives up after 10 seconds: shows the pending note once and starts the watch for this coach', async () => {
    useWatchClock();
    serveSubscriptionFlag(false);

    const rendered = renderProtectedShell('/billing/success');
    expect(await screen.findByRole('heading', { name: /./ })).toBeInTheDocument();
    expect(rendered.router.state.location.pathname).toBe('/billing/success');

    await advanceClock(rendered, 11_000, 1_000);

    expect(await screen.findByText('Settings page')).toBeInTheDocument();
    expect(rendered.router.state.location.pathname).toBe('/settings');
    expect(toastsShown(PENDING)).toBe(1);
    const watch = useCheckoutActivationStore.getState();
    expect(watch.startedAt).not.toBeNull();
    expect(watch.userId).toBe(COACH_ID);
    expect(watch.targetPlanCode).toBeNull();
    expect(storedWatch()).toContain(COACH_ID);

    // The note was announced once, and the watch did not repeat it while it kept looking for 15 minutes.
    await advanceClock(rendered, SLOW_PHASE_MS + SLOW_STEP_MS, SLOW_STEP_MS);
    expect(toastsShown(PENDING)).toBe(1);
    expect(toastsShown(ACTIVATED)).toBe(0);
  });
});
