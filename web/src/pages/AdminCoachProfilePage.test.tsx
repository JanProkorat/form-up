/**
 * '/admin/coach-accounts/:coachId' — the live refetch on a subscription-change push, the Stripe customer link and
 * the history labels of the plan-change kinds.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, screen, waitFor } from '@testing-library/react';
import { AdminCoachHistoryKind } from '@/api/generated';
import { COACH_ID, adminCoachPayload, renderAdminCoach, serveAdminCoach } from '@/test/adminCoach';

afterEach(() => {
  vi.unstubAllEnvs();
});

describe('AdminCoachProfilePage live refresh', () => {
  it('refetches the coach on a push for this coach and ignores other coaches', async () => {
    const state = serveAdminCoach();
    const { realtime } = renderAdminCoach();
    await screen.findByRole('heading', { name: 'Test Coach', level: 1 });
    await waitFor(() => expect(state.coachRequests).toBe(1));

    act(() => realtime.emit('admincoachsubscriptionchanged', { coachId: '22222222-2222-2222-2222-222222222222' }));
    act(() => realtime.emit('admincoachsubscriptionchanged', 'not an object'));
    act(() => realtime.emit('admincoachsubscriptionchanged', { coachId: COACH_ID }));

    // The matching push is the only one that fetches: exactly one more request, not three.
    await waitFor(() => expect(state.coachRequests).toBe(2));
    expect(state.coachRequests).toBe(2);
  });

  it('shows the new state after the push', async () => {
    const state = serveAdminCoach();
    const { realtime } = renderAdminCoach();
    await screen.findByRole('heading', { name: 'Test Coach', level: 1 });

    state.setCoach(adminCoachPayload({ name: 'Renamed Coach' }));
    act(() => realtime.emit('admincoachsubscriptionchanged', { coachId: COACH_ID }));

    expect(await screen.findByRole('heading', { name: 'Renamed Coach', level: 1 })).toBeInTheDocument();
  });

  it('refetches after the connection is re-established', async () => {
    const state = serveAdminCoach();
    const { realtime } = renderAdminCoach();
    await screen.findByRole('heading', { name: 'Test Coach', level: 1 });

    act(() => realtime.reconnect());

    await waitFor(() => expect(state.coachRequests).toBe(2));
  });
});

describe('AdminCoachProfilePage Stripe link', () => {
  it('links to the Stripe customer in a new tab when the coach has one', async () => {
    vi.stubEnv('VITE_STRIPE_DASHBOARD_URL', 'https://dashboard.stripe.com/');
    serveAdminCoach(adminCoachPayload({ stripeCustomerId: 'cus_a/b' }));
    renderAdminCoach();

    const link = await screen.findByRole('link', { name: 'Open in Stripe' });
    expect(link).toHaveAttribute('href', 'https://dashboard.stripe.com/customers/cus_a%2Fb');
    expect(link).toHaveAttribute('target', '_blank');
    expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    expect(screen.queryByText(/coming soon/i)).not.toBeInTheDocument();
  });

  it('is disabled when the coach has no Stripe customer', async () => {
    serveAdminCoach(adminCoachPayload({ stripeCustomerId: undefined }));
    renderAdminCoach();

    const button = await screen.findByRole('button', { name: 'Open in Stripe' });
    expect(button).toBeDisabled();
    expect(screen.queryByRole('link', { name: 'Open in Stripe' })).not.toBeInTheDocument();
    expect(screen.queryByText(/coming soon/i)).not.toBeInTheDocument();
  });
});

describe('AdminCoachProfilePage history', () => {
  it('labels the plan-change kinds, with a neutral label for an ended paid plan', async () => {
    const at = '2026-11-01T10:00:00Z';
    serveAdminCoach(
      adminCoachPayload({
        history: [
          { occurredAt: at, kind: AdminCoachHistoryKind.ChoseFreePlan, byCoach: true },
          { occurredAt: at, kind: AdminCoachHistoryKind.PaidPlanStarted, byCoach: true, planCode: 'pro' },
          { occurredAt: at, kind: AdminCoachHistoryKind.PaidPlanUpgraded, byCoach: true, planCode: 'pro' },
          { occurredAt: at, kind: AdminCoachHistoryKind.PaidPlanDowngraded, byCoach: true, planCode: 'pro' },
          { occurredAt: at, kind: AdminCoachHistoryKind.PaidPlanEnded, byCoach: true, planCode: 'pro' },
        ],
      }),
    );
    renderAdminCoach();

    expect(await screen.findByText('Chose the Free plan')).toBeInTheDocument();
    expect(screen.getByText('Paid plan started: Pro')).toBeInTheDocument();
    expect(screen.getByText('Paid plan upgraded: Pro')).toBeInTheDocument();
    expect(screen.getByText('Paid plan downgraded: Pro')).toBeInTheDocument();
    expect(screen.getByText('Paid plan ended')).toBeInTheDocument();
  });
});
