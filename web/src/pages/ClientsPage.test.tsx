/**
 * Clients page: tabs, the Filter menu and its chip, URL state, the plans popup and an ended link's row.
 * Ported from the Playwright spec (trainer/clients, #1066): the real-backend invite journey and the
 * geometry, animation and selection-bar tests stay there.
 */
import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import { ClientListStatus, PendingInviteStatus, PendingRowKind } from '@/api/generated';
import {
  TRAINING_PLAN,
  newClientsListState,
  qaClient,
  renderClientsPage,
  serveClientsList,
} from '@/test/clientsList';

/** Waits for the table to settle on the seeded client's row. */
async function rowSettled() {
  await screen.findByRole('link', { name: 'QA Client' });
}

describe('Clients page: tabs and URL state', () => {
  it('switching tabs updates the URL and the active tab', async () => {
    serveClientsList(newClientsListState());
    const { user, router } = renderClientsPage();
    await rowSettled();

    expect(screen.getByRole('tab', { name: /^Active/ })).toHaveAttribute('data-state', 'active');
    const allTab = screen.getByRole('tab', { name: /^All/ });
    await user.click(allTab);

    await waitFor(() => expect(router.state.location.search).toMatch(/tab=All/));
    expect(allTab).toHaveAttribute('data-state', 'active');

    // Table settles into either rows or the empty state, never stuck loading.
    await screen.findByRole('link', { name: 'QA Client' });
    expect(screen.queryByText(/Loading/)).not.toBeInTheDocument();
  });

  it('the Filter menu and its chip are hidden on the Pending tab', async () => {
    serveClientsList(newClientsListState());
    renderClientsPage({ url: '/clients?tab=Pending&plan=Active' });

    await waitFor(() => expect(screen.getByRole('tab', { name: /^Pending/ })).toHaveAttribute('data-state', 'active'));
    expect(screen.queryByRole('button', { name: /^Filter/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Remove filter/ })).not.toBeInTheDocument();
  });

  it.each(['tab=Paused', 'chip=EndingSoon', 'endingSoon=1', 'outsidePlan=1', 'tab=Paused&chip=EndingSoon'])(
    'the legacy ?%s URL falls back to the default tab with no plan',
    async (query) => {
      serveClientsList(newClientsListState());
      renderClientsPage({ url: `/clients?${query}` });
      await rowSettled();

      expect(screen.getByRole('tab', { name: /^Active/ })).toHaveAttribute('data-state', 'active');
      expect(screen.queryByRole('button', { name: /Remove filter/ })).not.toBeInTheDocument();
    },
  );

  it('a valid ?tab=Archived&plan=None URL opens that tab with the plan chip', async () => {
    serveClientsList(newClientsListState());
    renderClientsPage({ url: '/clients?tab=Archived&plan=None' });

    await waitFor(() => expect(screen.getByRole('tab', { name: /^Archived/ })).toHaveAttribute('data-state', 'active'));
    expect(await screen.findByRole('button', { name: 'Remove filter No plan' })).toBeInTheDocument();
  });
});

describe('Clients page: Filter menu', () => {
  it('picks one plan, which lands in the URL and as a removable chip', async () => {
    serveClientsList(newClientsListState());
    const { user, router } = renderClientsPage();
    await rowSettled();

    // The old chip row (All / Ending soon) is gone; the filter lives in one menu.
    expect(screen.queryByRole('button', { name: /Ending soon/ })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /^Filter/ }));
    expect(await screen.findByRole('menuitemradio', { name: /^All plans/ })).toHaveAttribute('aria-checked', 'true');
    expect(screen.getAllByRole('menuitemradio')).toHaveLength(4);

    // A zero count would disable the option; the seeded client has a current plan, so it is enabled.
    const activePlans = screen.getByRole('menuitemradio', { name: /^Active plans/ });
    expect(activePlans).not.toHaveAttribute('data-disabled');
    await user.click(activePlans);

    // A single choice closes the menu.
    await waitFor(() => expect(screen.queryByRole('menuitemradio')).not.toBeInTheDocument());
    expect(router.state.location.search).toMatch(/plan=Active/);
    expect(screen.getByRole('button', { name: /^Filter/ })).toHaveTextContent('1');

    // Switching tab keeps the plan choice.
    await user.click(screen.getByRole('tab', { name: /^All/ }));
    await waitFor(() => expect(router.state.location.search).toMatch(/tab=All/));
    expect(router.state.location.search).toMatch(/plan=Active/);

    await user.click(screen.getByRole('button', { name: 'Remove filter Active plans' }));
    await waitFor(() => expect(router.state.location.search).not.toMatch(/plan=/));
  });
});

describe('Clients page: pending rows', () => {
  const pendingRows = [
    {
      kind: PendingRowKind.Invite,
      publicId: '11111111-1111-1111-1111-111111111111',
      firstName: 'Waiting',
      lastName: 'Invitee',
      email: 'waiting.invitee@formup.test',
      sentAt: '2026-11-01T10:00:00Z',
      status: PendingInviteStatus.Pending,
    },
  ];

  it('the All tab shows no pending section or rows', async () => {
    serveClientsList(newClientsListState({ pending: pendingRows }));
    const { user, router } = renderClientsPage();
    await rowSettled();

    await user.click(screen.getByRole('tab', { name: /^All/ }));
    await waitFor(() => expect(router.state.location.search).toMatch(/tab=All/));
    await rowSettled();

    expect(screen.queryByRole('heading', { name: 'Pending' })).not.toBeInTheDocument();
    expect(screen.queryByText('Waiting Invitee')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Cancel invite' })).not.toBeInTheDocument();
  });

  it('the Pending tab lists the pending rows with their actions', async () => {
    serveClientsList(newClientsListState({ pending: pendingRows }));
    renderClientsPage({ url: '/clients?tab=Pending' });

    const row = await screen.findByRole('row', { name: /Waiting Invitee/ });
    expect(within(row).getByRole('button', { name: 'Cancel invite' })).toBeEnabled();
  });
});

describe('Clients page: rows', () => {
  /**
   * #1091: the plans popup states the plan type as text, with the plan name demoted to line 2.
   * The seeded trainer link grants training plans only, so the fixture client surfaces exactly one.
   */
  it('the plans popup states the plan type as text and demotes the name to line 2 (#1091)', async () => {
    serveClientsList(newClientsListState({ clients: [qaClient({ activePlans: [TRAINING_PLAN] })] }));
    const { user } = renderClientsPage();

    await user.hover(await screen.findByRole('button', { name: '1 active plan' }));

    const card = await waitFor(() => {
      const element = document.querySelector<HTMLElement>("[data-slot='hover-card-content']");
      expect(element).toBeInTheDocument();
      return element as HTMLElement;
    });
    expect(within(card).getByText('QA Client', { exact: false })).toBeVisible();
    expect(within(card).getByText('Training', { exact: true })).toBeVisible();
    expect(within(card).getByText('1 active plan', { exact: true })).toBeVisible();
    expect(within(card).getByText('QA Test Plan — ForTime fixture', { exact: false })).toBeVisible();
    expect(within(card).queryByText('Since', { exact: false })).not.toBeInTheDocument();
  });

  /** An ended link has no detail page (the API 404s it); the menu offers the kept payments instead (#1711). */
  it('an archived row with an ended link is not a link and its menu offers Payments, not the detail', async () => {
    const endedId = '99999999-9999-9999-9999-999999999999';
    serveClientsList(
      newClientsListState({
        // The Archived tab selects its list with `tab`, not the legacy `status` (#2086).
        listFor: (params) =>
          params.get('tab') === 'Archived'
            ? {
                clients: [
                  {
                    publicId: endedId,
                    userId: '88888888-8888-8888-8888-888888888888',
                    email: 'ended.link@formup.test',
                    firstName: 'Ended',
                    lastName: 'Link',
                    isActive: false,
                    status: ClientListStatus.Archived,
                    tags: [],
                    unreadMessageCount: 0,
                    activePlans: [],
                  },
                ],
                totalCount: 1,
                tabCounts: {},
                filterCounts: {},
              }
            : undefined,
      }),
    );
    const { user } = renderClientsPage();
    await rowSettled();

    await user.click(screen.getByRole('tab', { name: /Archived/ }));

    const row = await screen.findByRole('row', { name: /Ended Link/ });
    expect(within(row).queryByRole('link')).not.toBeInTheDocument();

    await user.click(within(row).getByRole('button', { name: 'Open row menu' }));
    expect(screen.queryByRole('menuitem', { name: 'Open client detail' })).not.toBeInTheDocument();
    expect(await screen.findByRole('menuitem', { name: 'Payments' })).toHaveAttribute('href', `/clients/${endedId}/payment`);
  });
});
