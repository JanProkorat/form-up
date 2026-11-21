import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import { checkIn, newCheckInsState, renderCheckInsPage, serveCheckIns } from '@/test/checkIns';

// Wednesday of ISO week 43 (19–25 Oct 2026). Only Date is faked so msw and user-event keep real timers.
const NOW = new Date('2026-10-21T10:00:00Z');

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(NOW);
});

afterEach(() => {
  vi.useRealTimers();
});

/** The table row that shows this client's name. */
async function rowOf(name: string) {
  const link = await screen.findByRole('link', { name });
  const row = link.closest('tr');
  if (!row) {
    throw new Error(`No row for ${name}`);
  }
  return within(row);
}

describe('CheckInsPage', () => {
  it('maps backend statuses onto the board states and totals them', async () => {
    const state = newCheckInsState({
      rows: [
        checkIn({ clientName: 'Eva Svobodová', status: 'Responded' }),
        checkIn({ clientName: 'Jakub Horák', status: 'Expired', respondedAt: undefined }),
        checkIn({ clientName: 'Ondřej Kučera', status: 'Pending', respondedAt: undefined }),
        checkIn({ clientName: 'Klára Veselá', status: 'Reviewed', reviewedByTrainerAt: '2026-10-20T08:00:00Z' }),
        checkIn({ clientName: 'Unknown Status', status: 'Dismissed', respondedAt: undefined }),
      ],
    });
    serveCheckIns(state);
    renderCheckInsPage();

    expect((await rowOf('Eva Svobodová')).getByText('Not reviewed')).toBeInTheDocument();
    expect((await rowOf('Jakub Horák')).getByText('Missed')).toBeInTheDocument();
    expect((await rowOf('Ondřej Kučera')).getByText('Waiting')).toBeInTheDocument();
    expect((await rowOf('Klára Veselá')).getByText('Reviewed')).toBeInTheDocument();
    // An unknown status counts as Waiting instead of crashing.
    expect((await rowOf('Unknown Status')).getByText('Waiting')).toBeInTheDocument();

    expect(screen.getByText('2 of 5')).toBeInTheDocument();
    expect(screen.getByText('40 % of sent check-ins')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'All 5' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Not reviewed 1' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Missed 1' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Waiting 2' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reviewed 1' })).toBeInTheDocument();
    expect(screen.getByText('Viewing 5 of 5', { exact: false })).toBeInTheDocument();
  });

  it('shows sublines and offers only the actions each state allows', async () => {
    const state = newCheckInsState({
      rows: [
        checkIn({ clientName: 'Eva Svobodová', status: 'Responded' }),
        checkIn({ clientName: 'Jakub Horák', status: 'Expired', respondedAt: undefined, dueAt: '2026-10-18T16:00:00Z' }),
        checkIn({ clientName: 'Ondřej Kučera', status: 'Pending', respondedAt: undefined, dueAt: '2026-10-21T16:00:00Z' }),
        checkIn({ clientName: 'Klára Veselá', status: 'Reviewed', reviewedByTrainerAt: '2026-10-17T08:00:00Z' }),
      ],
    });
    serveCheckIns(state);
    renderCheckInsPage();

    const notReviewed = await rowOf('Eva Svobodová');
    expect(notReviewed.getByRole('link', { name: 'Open answers' })).toBeInTheDocument();
    expect(notReviewed.getByRole('button', { name: 'Mark reviewed' })).toBeInTheDocument();

    const missed = await rowOf('Jakub Horák');
    expect(missed.getByText(/Answer window ended Sun 18 Oct/)).toBeInTheDocument();
    expect(missed.getByRole('link', { name: 'Write to Jakub' })).toHaveAttribute('href', `/inbox?client=${state.rows[1]?.clientPublicId}`);
    expect(missed.queryByRole('link', { name: 'Open answers' })).not.toBeInTheDocument();
    expect(missed.queryByRole('button', { name: 'Mark reviewed' })).not.toBeInTheDocument();
    expect(missed.getByText('—')).toBeInTheDocument();

    const waiting = await rowOf('Ondřej Kučera');
    expect(waiting.getByText(/Can answer until Wed 21 Oct/)).toBeInTheDocument();
    expect(waiting.queryByRole('link', { name: /Open answers|Write to/ })).not.toBeInTheDocument();
    expect(waiting.queryByRole('button')).not.toBeInTheDocument();

    const reviewed = await rowOf('Klára Veselá');
    expect(reviewed.getByText(/Reviewed Sat 17 Oct/)).toBeInTheDocument();
    expect(reviewed.getByRole('link', { name: 'Open answers' })).toBeInTheDocument();
    expect(reviewed.queryByRole('button', { name: 'Mark reviewed' })).not.toBeInTheDocument();
  });

  it('filters the table with the status chips', async () => {
    const state = newCheckInsState({
      rows: [
        checkIn({ clientName: 'Eva Svobodová', status: 'Responded' }),
        checkIn({ clientName: 'Jakub Horák', status: 'Expired', respondedAt: undefined }),
        checkIn({ clientName: 'Lucie Marková', status: 'Expired', respondedAt: undefined }),
      ],
    });
    serveCheckIns(state);
    const { user } = renderCheckInsPage();

    await screen.findByRole('link', { name: 'Eva Svobodová' });
    await user.click(screen.getByRole('button', { name: 'Missed 2' }));

    expect(screen.getByRole('button', { name: 'Missed 2' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.queryByRole('link', { name: 'Eva Svobodová' })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Jakub Horák' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Lucie Marková' })).toBeInTheDocument();
    expect(screen.getByText('Viewing 2 of 3', { exact: false })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Reviewed 0' }));
    expect(screen.getByText('No check-ins match this filter.')).toBeInTheDocument();
  });

  it('defaults to the current ISO week and sends the Monday of the shown week', async () => {
    const state = newCheckInsState({ rows: [checkIn({ clientName: 'Eva Svobodová' })] });
    serveCheckIns(state);
    const { user } = renderCheckInsPage();

    expect(await screen.findByText('Week 43 · 19–25 Oct')).toBeInTheDocument();
    expect(state.listWeeks).toEqual(['2026-10-19']);

    await user.click(screen.getByRole('button', { name: 'Previous week' }));
    expect(await screen.findByText('Week 42 · 12–18 Oct')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Next week' }));
    await user.click(screen.getByRole('button', { name: 'Next week' }));
    expect(await screen.findByText('Week 44 · 26 Oct – 1 Nov')).toBeInTheDocument();

    expect(state.listWeeks).toContain('2026-10-12');
    expect(state.listWeeks).toContain('2026-10-26');
    expect(state.listWeeks[state.listWeeks.length - 1]).toBe('2026-10-26');
  });

  it('links Open answers to the client Forms tab without marking the check-in reviewed', async () => {
    const row = checkIn({ clientName: 'Eva Svobodová', status: 'Responded' });
    const state = newCheckInsState({ rows: [row] });
    serveCheckIns(state);
    const { user, router } = renderCheckInsPage();

    const link = (await rowOf('Eva Svobodová')).getByRole('link', { name: 'Open answers' });
    const target = `/clients/${row.clientPublicId}/forms?form=${row.id}`;
    expect(link).toHaveAttribute('href', target);
    await user.click(link);

    expect(await screen.findByTestId('forms-tab')).toBeInTheDocument();
    expect(router.state.location.pathname + router.state.location.search).toBe(target);
    expect(state.marked).toEqual([]);
  });

  it('marks a check-in reviewed once and refreshes the list', async () => {
    const row = checkIn({ clientName: 'Eva Svobodová', status: 'Responded' });
    const state = newCheckInsState({ rows: [row] });
    serveCheckIns(state);
    const { user } = renderCheckInsPage();

    const button = (await rowOf('Eva Svobodová')).getByRole('button', { name: 'Mark reviewed' });
    await user.click(button);
    // Disabled while the request is in flight, so a second click cannot post twice.
    expect(button).toBeDisabled();

    const after = await rowOf('Eva Svobodová');
    expect(await after.findByText(/Reviewed Tue 20 Oct/)).toBeInTheDocument();
    expect(after.queryByRole('button', { name: 'Mark reviewed' })).not.toBeInTheDocument();
    expect(state.marked).toEqual([row.id]);
    expect(state.listWeeks).toHaveLength(2);
    expect(screen.getByRole('button', { name: 'Reviewed 1' })).toBeInTheDocument();
  });

  it('shows a toast and leaves the row alone when mark reviewed is rejected', async () => {
    const row = checkIn({ clientName: 'Eva Svobodová', status: 'Responded' });
    const state = newCheckInsState({ rows: [row], markStatus: 404 });
    serveCheckIns(state);
    const { user } = renderCheckInsPage();

    await user.click((await rowOf('Eva Svobodová')).getByRole('button', { name: 'Mark reviewed' }));

    expect(await screen.findByText('Could not mark the check-in as reviewed.')).toBeInTheDocument();
    const after = await rowOf('Eva Svobodová');
    expect(after.getByText('Not reviewed')).toBeInTheDocument();
    expect(after.getByRole('button', { name: 'Mark reviewed' })).toBeEnabled();
    expect(state.listWeeks).toHaveLength(1);
  });

  it('lists clients inside the plan above those outside it, by name', async () => {
    const state = newCheckInsState({
      rows: [
        checkIn({ clientName: 'Anna Outside', clientIsOutsidePlan: true }),
        checkIn({ clientName: 'Zdenek Inside' }),
        checkIn({ clientName: 'Bob Inside' }),
        checkIn({ clientName: 'Cyril Outside', clientIsOutsidePlan: true }),
      ],
    });
    serveCheckIns(state);
    renderCheckInsPage();

    await screen.findByRole('link', { name: 'Bob Inside' });
    const names = screen
      .getAllByRole('link', { name: /Inside|Outside/ })
      .map((link) => link.textContent);
    expect(names).toEqual(['Bob Inside', 'Zdenek Inside', 'Anna Outside', 'Cyril Outside']);
  });

  it('formats each row in the client time zone', async () => {
    const state = newCheckInsState({
      rows: [
        // 16:00 UTC is 18:00 in Prague (CEST) and 12:00 in New York (EDT).
        checkIn({ clientName: 'Prague Client', clientTimeZone: 'Europe/Prague' }),
        checkIn({ clientName: 'New York Client', clientTimeZone: 'America/New_York' }),
        // 23:30 UTC on the 16th is already the 17th in Sydney.
        checkIn({ clientName: 'Sydney Client', clientTimeZone: 'Australia/Sydney', sentAt: '2026-10-16T23:30:00Z' }),
      ],
    });
    serveCheckIns(state);
    renderCheckInsPage();

    expect((await rowOf('Prague Client')).getByText(/Fri 16 Oct, 18:00/)).toBeInTheDocument();
    expect((await rowOf('New York Client')).getByText(/Fri 16 Oct, 12:00/)).toBeInTheDocument();
    expect((await rowOf('Sydney Client')).getByText(/Sat 17 Oct, 10:30/)).toBeInTheDocument();
    expect(screen.getByText('Times follow each client’s time zone')).toBeInTheDocument();
  });

  it('explains an empty week without naming a weekday', async () => {
    serveCheckIns(newCheckInsState());
    renderCheckInsPage();

    const message = await screen.findByText(/No check-ins for this week yet/);
    expect(message.textContent).not.toMatch(/monday|tuesday|wednesday|thursday|friday|saturday|sunday/i);
    expect(screen.getByText('0 of 0')).toBeInTheDocument();
  });

  it('shows an error with a retry when the list fails to load', async () => {
    const state = newCheckInsState({ rows: [checkIn({ clientName: 'Eva Svobodová' })], listStatus: 500 });
    serveCheckIns(state);
    const { user } = renderCheckInsPage();

    expect(await screen.findByText('Could not load the check-ins.')).toBeInTheDocument();

    state.listStatus = undefined;
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('link', { name: 'Eva Svobodová' })).toBeInTheDocument();
  });

  it('shows the paywall instead of the list when the plan has no check-ins', async () => {
    serveCheckIns(newCheckInsState({ features: { canUseWeeklyCheckIns: false } }));
    renderCheckInsPage();

    expect(await screen.findByRole('region', { name: /check-in/i })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Previous week' })).not.toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });
});
