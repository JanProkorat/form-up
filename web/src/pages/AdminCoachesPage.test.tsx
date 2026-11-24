/**
 * '/admin/coach-accounts' — the deep links the Overview sends: ?status= picks a filter (an unknown value falls back
 * to All) and ?q= pre-fills the search. The very first list request already carries them.
 */
import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { renderAdminCoachesList, serveAdminCoachesList } from '@/test/adminCoach';

describe('AdminCoachesPage deep links', () => {
  it.each([
    ['PaymentFailed', 'Payment failed'],
    ['Cancelling', 'Cancelling'],
    ['GivenFree', 'Set by Form Up'],
  ])('opens with the %s filter selected', async (status, label) => {
    const state = serveAdminCoachesList();
    renderAdminCoachesList(`?status=${status}`);

    expect(await screen.findByRole('button', { name: new RegExp(`^${label}`), pressed: true })).toBeInTheDocument();
    expect(state.requests[0]?.get('status')).toBe(status);
    expect(state.requests.every((query) => query.get('status') === status)).toBe(true);
  });

  it('falls back to All for an unknown status', async () => {
    const state = serveAdminCoachesList();
    renderAdminCoachesList('?status=Bogus');

    expect(await screen.findByRole('button', { name: /^All/, pressed: true })).toBeInTheDocument();
    expect(state.requests[0]?.get('status')).toBe('All');
  });

  it('pre-fills the search from ?q= and sends it with the first request', async () => {
    const state = serveAdminCoachesList();
    renderAdminCoachesList('?q=klara');

    expect(await screen.findByRole('searchbox', { name: 'Search coaches' })).toHaveValue('klara');
    expect(await screen.findByText('No coaches match.')).toBeInTheDocument();
    expect(state.requests[0]?.get('search')).toBe('klara');
    expect(state.requests.every((query) => query.get('search') === 'klara')).toBe(true);
  });
});
