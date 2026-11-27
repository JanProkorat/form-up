/**
 * Shared harness for the Inbox tests: renders the real InboxPage on `/inbox` and serves the requests it makes
 * (the conversation list, filter counts, threads, mark-read and the client panel) from one mutable `InboxState`.
 * A test changes the state (or adds its own `server.use(...)` handler, which wins over these) and then asserts
 * on what the coach sees. Payloads are typed with the generated API types, so a changed contract breaks the
 * typecheck.
 *
 * The page needs no SignalR stub: `useSignalR` only subscribes to the app-wide connection that
 * RealtimeProvider owns, and renderWithProviders mounts none, so no connection is ever opened.
 */
import { Outlet } from 'react-router-dom';
import type { RouteObject } from 'react-router-dom';
import { ChatMessageKind } from '@/api/generated';
import type {
  ConversationDto,
  GetClientDashboardResponse,
  GetConversationFilterCountsResponse,
  GetMySubscriptionResponse,
  ListClientPlansResponse,
  MessageDto,
} from '@/api/generated';
import { Toaster } from '@/components/ui/toast';
import InboxPage from '@/pages/InboxPage';
import { useToastStore } from '@/stores/toast';
import { HttpResponse, apiUrl, http, problem } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';

/** The signed-in coach, as renderWithProviders seeds the auth store. */
export const COACH_USER_ID = '00000000-0000-0000-0000-000000000001';

export const QA_CLIENT = {
  conversationId: 'aaaaaaaa-0000-0000-0000-0000000000c1',
  userId: '22222222-2222-2222-2222-2222222222c1',
  clientPublicId: '11111111-1111-1111-1111-1111111111c1',
};

export const QA_FORMER = {
  conversationId: 'aaaaaaaa-0000-0000-0000-0000000000c2',
  userId: '22222222-2222-2222-2222-2222222222c2',
  clientPublicId: '11111111-1111-1111-1111-1111111111c2',
};

export interface InboxState {
  /** The Active view, newest activity first. */
  conversations: ConversationDto[];
  /** The Archived view. */
  archived: ConversationDto[];
  /** Messages per conversation id, newest first (the API's order). */
  messages: Record<string, MessageDto[]>;
  counts: GetConversationFilterCountsResponse;
  /** The client panel's dashboard per client public id; an id without one answers 404 (an ended link). */
  dashboards: Record<string, GetClientDashboardResponse>;
  plans: ListClientPlansResponse;
  /** Conversation ids the page POSTed `/read` for, in order. */
  readRequests: string[];
}

/** A conversation with a client; the unread count is cleared by mark-read. */
export function conversation(
  ids: { conversationId: string; userId: string; clientPublicId: string },
  name: string,
  overrides: Partial<ConversationDto> = {},
): ConversationDto {
  return {
    id: ids.conversationId,
    participant: {
      id: ids.userId,
      name,
      initials: name
        .split(' ')
        .map((part) => part[0])
        .join(''),
      online: false,
      clientPublicId: ids.clientPublicId,
      roles: [],
    },
    lastMessage: 'See you on Monday',
    lastMessageAt: '2026-11-17T08:01:00Z',
    lastMessageIsOwn: false,
    unreadCount: 0,
    isFormer: false,
    isSendLocked: false,
    ...overrides,
  };
}

/** A plain text message. */
export function message(id: string, senderId: string, text: string, timestamp: string): MessageDto {
  return { id, senderId, text, timestamp, isRead: true, kind: ChatMessageKind.Text };
}

/** The seeded inbox: an unread thread with QA Client (first row) and one with the ended-link QA Former. */
export function newInboxState(overrides: Partial<InboxState> = {}): InboxState {
  return {
    conversations: [
      conversation(QA_CLIENT, 'QA Client', { unreadCount: 1 }),
      conversation(QA_FORMER, 'QA Former', {
        unreadCount: 2,
        isFormer: true,
        lastMessageAt: '2026-11-16T08:01:00Z',
      }),
    ],
    archived: [],
    messages: {
      [QA_CLIENT.conversationId]: [
        message('m3', QA_CLIENT.userId, 'How was the session?', '2026-11-17T08:01:00Z'),
        message('m2', COACH_USER_ID, 'Great work this week', '2026-11-16T09:00:00Z'),
        message('m1', QA_CLIENT.userId, 'Hi coach', '2026-11-16T08:00:00Z'),
      ],
      [QA_FORMER.conversationId]: [message('f1', QA_FORMER.userId, 'Thanks for everything', '2026-11-16T08:01:00Z')],
    },
    counts: { all: 2, unreadMessages: 2, noMessages: 0, newCheckIns: 0, missingCheckIns: 0, endingSoon: 0 },
    dashboards: {},
    plans: { plans: [], canViewNutritionPlans: false, canViewTrainingPlans: false },
    readRequests: [],
    ...overrides,
  };
}

const subscription: GetMySubscriptionResponse = { features: {} };

/** Serves the page's own requests from `state`; everything else is declared by the test. */
export function serveInbox(state: InboxState): void {
  const find = (id: string | readonly string[] | undefined) =>
    [...state.conversations, ...state.archived].find((c) => c.id === id);

  server.use(
    http.get(apiUrl('/conversations'), ({ request }) => {
      const archived = new URL(request.url).searchParams.get('archived') === 'true';
      return HttpResponse.json(archived ? state.archived : state.conversations);
    }),
    http.get(apiUrl('/conversations/filter-counts'), () => HttpResponse.json(state.counts)),
    http.get(apiUrl('/conversations/:id/messages'), ({ params }) =>
      HttpResponse.json({ items: state.messages[String(params['id'])] ?? [] }),
    ),
    http.post(apiUrl('/conversations/:id/read'), ({ params }) => {
      state.readRequests.push(String(params['id']));
      const target = find(params['id']);
      if (target) {
        target.unreadCount = 0;
      }
      return new HttpResponse(null, { status: 204 });
    }),
    http.get(apiUrl('/users/me/subscription'), () => HttpResponse.json(subscription)),
    http.get(apiUrl('/trainer/clients/:clientId'), ({ params }) => {
      const dashboard = state.dashboards[String(params['clientId'])];
      return dashboard ? HttpResponse.json(dashboard) : problem(404, 'NOT_LINKED_TO_CLIENT');
    }),
    http.get(apiUrl('/trainer/clients/:clientId/plans'), () => HttpResponse.json(state.plans)),
    http.get(apiUrl('/trainer/clients/:clientId/measurements'), () =>
      HttpResponse.json({ items: [], totalCount: 0, page: 1, pageSize: 50 }),
    ),
  );
}

/** jsdom has no matchMedia; the page docks its client panel at 1280px and wider, so every query matches. */
function stubWideViewport() {
  window.matchMedia = (query: string): MediaQueryList => ({
    matches: true,
    media: query,
    onchange: null,
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: () => false,
  });
}

interface RenderInboxOptions {
  /** Path and query to open. */
  url?: string;
  /** Extra routes, e.g. the clients list or a client-detail stand-in. */
  routes?: RouteObject[];
}

/** Mounts InboxPage on `/inbox` at a docked-panel viewport, with the toast outlet the app shell provides. */
export function renderInbox({ url = '/inbox', routes = [] }: RenderInboxOptions = {}) {
  useToastStore.setState({ toasts: [] });
  stubWideViewport();
  const tree: RouteObject[] = [
    {
      element: (
        <>
          <Outlet />
          <Toaster />
        </>
      ),
      children: [{ path: '/inbox', element: <InboxPage /> }, ...routes],
    },
  ];
  return renderWithProviders(tree, { initialEntries: [url], auth: { roles: ['Trainer'] } });
}
