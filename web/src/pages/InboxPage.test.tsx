/**
 * Inbox page: selection on entry, the empty state, search, the Active/Archived view, the filter menu's disabled
 * rows, the YouTube preview card and the client panel's status pill. Ported from the Playwright spec
 * (trainer/inbox, #1095): the real-backend send journey, the live-filter parity checks, the SignalR push and
 * typing test, the scroll test and the client-detail round trips stay there.
 */
import { describe, expect, it } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import type { ClientPlanItem, GetClientDashboardResponse } from '@/api/generated';
import { ClientListStatus } from '@/api/generated';
import type { RawTrainingPlanDetail } from '@/api/training-plan-types';
import ClientsPage from '@/pages/ClientsPage';
import { newClientsListState, serveClientsList } from '@/test/clientsList';
import {
  COACH_USER_ID,
  QA_CLIENT,
  QA_FORMER,
  message,
  newInboxState,
  renderInbox,
  serveInbox,
} from '@/test/inbox';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

/** The inbox list row button of a conversation, found by the row's name text. */
function row(name: string): HTMLElement {
  const nameElement = screen.getByText(name, { exact: true, selector: '[data-testid="inbox-row-name"]' });
  return nameElement.closest('button') as HTMLElement;
}

const queryRow = (name: string) =>
  screen.queryByText(name, { exact: true, selector: '[data-testid="inbox-row-name"]' })?.closest('button') ?? null;

describe('Inbox: entry and selection', () => {
  it('the first conversation is selected and its thread is open on entry', async () => {
    const state = newInboxState();
    serveInbox(state);
    const { user, queryClient } = renderInbox();

    await screen.findByText('QA Client', { exact: true, selector: '[data-testid="inbox-row-name"]' });
    expect(await screen.findByPlaceholderText('Type your message here...')).toBeVisible();
    expect(screen.queryByText('Select a chat to begin messaging')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Hide client' })).toBeVisible();
    // The open thread's header shows the first row's participant (the row itself also carries this text).
    const headerName = screen
      .getAllByText('QA Client', { exact: true })
      .find((element) => !element.matches('[data-testid="inbox-row-name"]'));
    expect(headerName).toBeVisible();

    // Merely entering the inbox must not mark the auto-selected thread read.
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    expect(state.readRequests).toHaveLength(0);

    // The first interaction inside the thread pane marks exactly that conversation read.
    await user.click(screen.getByTestId('thread-message-list'));
    await waitFor(() => expect(state.readRequests).toHaveLength(1));
    expect(state.readRequests[0]).toBe(QA_CLIENT.conversationId);
  });

  it('empty-state copy matches the wireframe when the selected chat is not in the current list', async () => {
    serveInbox(newInboxState());
    const { user } = renderInbox();

    // The first row is auto-selected on entry; the Archived view (nothing there) no longer contains it,
    // so the thread pane falls back to the empty state.
    expect(await screen.findByPlaceholderText('Type your message here...')).toBeVisible();
    await user.click(screen.getByRole('button', { name: /Active/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Archived' }));

    expect(await screen.findByText('Select a chat to begin messaging')).toBeVisible();
    expect(screen.getByText('Your client inbox, workout questions, and onboarding metrics live here.')).toBeVisible();
  });
});

describe('Inbox: list', () => {
  it('list renders with unread count badge; opening the thread marks it read', async () => {
    serveInbox(newInboxState());
    const { user } = renderInbox();

    // QA Client is auto-opened on entry but stays unread until the trainer interacts; QA Former keeps
    // its unread messages until the trainer opens that thread.
    const formerRow = await waitFor(() => row('QA Former'));
    // Unread badge: a red count (a plain message counts, a cooperation event row does not).
    expect(within(formerRow).getByRole('status')).toBeVisible();
    expect(within(formerRow).getByTestId('inbox-row-unread-count')).toHaveTextContent(/^\d+\+?$/);

    await user.click(formerRow);
    expect(screen.getAllByText('QA Former')[0]).toBeVisible();

    // Read mark round-trips through the backend + a list invalidation: the unread badge is gone.
    await waitFor(() => expect(within(row('QA Former')).queryByRole('status')).not.toBeInTheDocument());
  });

  it('marking a conversation read also invalidates the cached clients list that carries the unread count', async () => {
    const state = newInboxState();
    serveInbox(state);
    const { user, queryClient } = renderInbox();
    // A cached clients list from an earlier visit (no observer here, so only the invalidation flag can show it).
    const clientsKey = ['clients', 'list', { page: 1 }] as const;
    queryClient.setQueryData(clientsKey, { items: [] });

    const formerRow = await waitFor(() => row('QA Former'));
    await user.click(formerRow);
    await waitFor(() => expect(state.readRequests).toContain(QA_FORMER.conversationId));

    await waitFor(() => expect(queryClient.getQueryState(clientsKey)?.isInvalidated).toBe(true));
  });

  it('search narrows the list by participant name', async () => {
    serveInbox(newInboxState());
    const { user } = renderInbox();
    await waitFor(() => expect(queryRow('QA Client')).toBeVisible());

    await user.type(screen.getByPlaceholderText('Search chats...'), 'zzz-no-such-participant');
    expect(screen.getByText('No conversations found')).toBeVisible();
    expect(queryRow('QA Client')).not.toBeInTheDocument();

    await user.clear(screen.getByPlaceholderText('Search chats...'));
    await user.type(screen.getByPlaceholderText('Search chats...'), 'QA Client');
    expect(queryRow('QA Client')).toBeVisible();
  });

  it('the Active/Archived switch changes the visible rows', async () => {
    serveInbox(newInboxState());
    const { user } = renderInbox();
    await waitFor(() => expect(queryRow('QA Client')).toBeVisible());

    await user.click(screen.getByRole('button', { name: /Active/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Archived' }));

    // Nothing is archived: the active-view row disappears.
    await waitFor(() => expect(queryRow('QA Client')).not.toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /Archived/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Active' }));
    await waitFor(() => expect(queryRow('QA Client')).toBeVisible());
  });

  it('the three domain-less filters render disabled with the coming-soon tooltip', async () => {
    serveInbox(newInboxState());
    const { user } = renderInbox();

    await user.click(await screen.findByRole('button', { name: /^All \(\d+\)$/ }));

    for (const label of ['Failed payments', 'Blocked automations', 'Tasks overdue']) {
      const item = screen.getByRole('menuitem', { name: new RegExp(label) });
      expect(item).toBeVisible();
      expect(item).toHaveAttribute('aria-disabled', 'true');
      expect(item).toHaveAttribute('title', 'This page is coming soon.');
    }
  });
});

describe('Inbox: thread and client panel', () => {
  it('a YouTube link in message text renders as an embedded preview card', async () => {
    const state = newInboxState();
    state.messages[QA_CLIENT.conversationId] = [
      message('m4', COACH_USER_ID, 'Form check https://www.youtube.com/watch?v=dQw4w9WgXcQ', '2026-11-17T09:00:00Z'),
      ...state.messages[QA_CLIENT.conversationId],
    ];
    serveInbox(state);
    const { user } = renderInbox();

    await user.click(await waitFor(() => row('QA Client')));

    await waitFor(() => {
      const thumbnail = document.querySelector('img[src*="img.youtube.com"]');
      expect(thumbnail).toBeInTheDocument();
      expect(thumbnail).toBeVisible();
    });
  });

  it('the Show-client panel status pill equals the clients-list status pill, and the panel mirrors the week caption', async () => {
    const trainingPlan: ClientPlanItem = {
      planId: '33333333-3333-3333-3333-333333333333',
      planType: 'Training',
      status: 'Active',
      name: 'QA Test Plan',
    };
    const dashboard: GetClientDashboardResponse = {
      clientPublicId: QA_CLIENT.clientPublicId,
      clientUserId: QA_CLIENT.userId,
      firstName: 'QA',
      lastName: 'Client',
      isActive: true,
      status: ClientListStatus.Active,
      canViewTrainingPlans: true,
      canViewNutritionPlans: false,
      sessionsCompletedThisWeek: 2,
      sessionsPlannedThisWeek: 3,
    };
    const planDetail: RawTrainingPlanDetail = {
      planId: trainingPlan.planId as string,
      clientId: QA_CLIENT.clientPublicId,
      trainerId: COACH_USER_ID,
      name: 'QA Test Plan',
      status: 'Active',
      version: 1,
      dateCreated: '2026-01-01T00:00:00Z',
      weeks: [],
    };
    const state = newInboxState({
      dashboards: { [QA_CLIENT.clientPublicId]: dashboard },
      plans: { plans: [trainingPlan], canViewTrainingPlans: true, canViewNutritionPlans: false },
    });
    serveInbox(state);
    serveClientsList(newClientsListState());
    server.use(http.get(apiUrl(`/training/plans/${trainingPlan.planId}`), () => HttpResponse.json(planDetail)));

    const { user, router } = renderInbox({
      url: '/clients',
      routes: [
        { path: '/clients', element: <ClientsPage /> },
        { path: '/clients/:clientId', element: <div data-testid="client-detail" /> },
      ],
    });

    // The list pill reads "Active plans" where the panel pill reads "Active"; "No plan" and "Archived" match.
    const listRow = (await screen.findByRole('link', { name: 'QA Client' })).closest('tr') as HTMLElement;
    const listStatusText = (listRow.querySelector('[data-slot="badge"]')?.textContent ?? '')
      .trim()
      .replace(/^Active plans$/, 'Active');
    expect(listStatusText).toBe('Active');

    await act(() => router.navigate('/inbox'));
    await user.click(await waitFor(() => row('QA Client')));

    // The panel is docked and open; "Hide client" closes it and "Show client" brings it back.
    const panel = await screen.findByRole('complementary', { name: 'Client details' });
    await user.click(screen.getByRole('button', { name: 'Hide client' }));
    // The aside stays mounted at width 0 with aria-hidden + inert, so role queries no longer see it.
    await waitFor(() => expect(screen.queryByRole('complementary', { name: 'Client details' })).not.toBeInTheDocument());
    await user.click(screen.getByRole('button', { name: 'Show client' }));
    const shownPanel = await screen.findByRole('complementary', { name: 'Client details' });
    expect(shownPanel).toBe(panel);
    expect(shownPanel.querySelector('[data-slot="badge"]')).toHaveTextContent(listStatusText);

    // The caption mirrors the dashboard numbers.
    expect(await within(shownPanel).findByText('Done this week · 2 of 3 sessions', { exact: true })).toBeVisible();

    // "Open client profile" is a link to the client's detail page.
    const profileLink = screen.getByRole('link', { name: 'Open client profile' });
    expect(profileLink).toHaveAttribute('href', `/clients/${QA_CLIENT.clientPublicId}`);
    await user.click(profileLink);
    await waitFor(() => expect(router.state.location.pathname).toBe(`/clients/${QA_CLIENT.clientPublicId}`));
  });
});
