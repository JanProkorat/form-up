/**
 * Shared harness for the Clients page tests: renders the real ClientsPage on `/clients` and serves the requests
 * it makes from one mutable `ClientsListState`. A test changes the state (or adds its own `server.use(...)`
 * handler, which wins over these) and then asserts on what the coach sees or on what the page sent.
 * Payloads are typed with the generated API types, so a changed contract breaks the typecheck.
 */
import { Outlet } from 'react-router-dom';
import type { RouteObject } from 'react-router-dom';
import { ClientListStatus, PackageBillingInterval, PaymentCurrency, Profession } from '@/api/generated';
import type {
  CheckPendingInviteSlotResponse,
  ClientPlanCounts,
  ClientSummary,
  ClientTabCounts,
  ClientTagDto,
  CoachDiscountDto,
  CoachPackageDto,
  GetClientsResponse,
  GetMySubscriptionResponse,
  PendingClientRow,
  QuestionnaireSummaryDto,
} from '@/api/generated';
import { Toaster } from '@/components/ui/toast';
import ClientsPage from '@/pages/ClientsPage';
import { useToastStore } from '@/stores/toast';
import { HttpResponse, apiUrl, http, problem } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';

export const QA_CLIENT_ID = '11111111-1111-1111-1111-1111111111c1';
const QA_CLIENT_USER_ID = '22222222-2222-2222-2222-2222222222c1';

export interface ClientsListState {
  clients: ClientSummary[];
  tabCounts: ClientTabCounts;
  planCounts: ClientPlanCounts;
  pending: PendingClientRow[];
  tags: ClientTagDto[];
  packages: CoachPackageDto[];
  discounts: CoachDiscountDto[];
  forms: QuestionnaireSummaryDto[];
  /** The answer to GET /trainer/pending-invites/slot-check: a body, or an error status. */
  slotCheck: CheckPendingInviteSlotResponse | number;
  /** Replaces the list response for the requests it returns a body for (e.g. only for `tab=Archived`). */
  listFor?: (params: URLSearchParams) => Partial<GetClientsResponse> | undefined;
  /** Every `GET /trainer/clients` query the page made, in order. */
  listQueries: URLSearchParams[];
  /** Every body POSTed to /trainer/pending-invites. */
  invites: Record<string, unknown>[];
  /** The email of every slot check the page made. */
  slotChecks: string[];
}

/** The seeded "QA Client": linked, Active, one training plan. */
export function qaClient(overrides: Partial<ClientSummary> = {}): ClientSummary {
  return {
    publicId: QA_CLIENT_ID,
    userId: QA_CLIENT_USER_ID,
    email: 'qa.client@formup.test',
    firstName: 'QA',
    lastName: 'Client',
    isActive: true,
    status: ClientListStatus.Active,
    tags: [],
    unreadMessageCount: 0,
    activePlans: [],
    ...overrides,
  };
}

export function newClientsListState(overrides: Partial<ClientsListState> = {}): ClientsListState {
  return {
    clients: [qaClient()],
    tabCounts: { all: 1, inPlan: 1, outsidePlan: 0, pending: 0, archived: 0 },
    planCounts: { all: 1, active: 1, none: 0, endingSoon: 0 },
    pending: [],
    tags: [],
    packages: [],
    discounts: [],
    forms: [],
    slotCheck: { nutritionTaken: false, trainingTaken: false },
    listQueries: [],
    invites: [],
    slotChecks: [],
    ...overrides,
  };
}

/** A coach package as the offer tiles list it. */
export function coachPackage(overrides: Partial<CoachPackageDto> = {}): CoachPackageDto {
  return {
    packageId: 'bbbbbbbb-0000-0000-0000-000000001907',
    name: 'Monthly coaching',
    billing: PackageBillingInterval.Monthly,
    price: 1800,
    currency: PaymentCurrency.CZK,
    showOnProfile: true,
    clientCount: 0,
    ...overrides,
  };
}

/** The training plan of the seeded client, as the plans popup lists it. */
export const TRAINING_PLAN = { name: 'QA Test Plan — ForTime fixture', type: Profession.Training };

const subscription: GetMySubscriptionResponse = { features: {} };

/** Serves the page's own requests from `state`; everything else is declared by the test. */
export function serveClientsList(state: ClientsListState): void {
  let nextTagNumber = 1;
  server.use(
    http.get(apiUrl('/trainer/clients'), ({ request }) => {
      const params = new URL(request.url).searchParams;
      state.listQueries.push(params);
      const body: GetClientsResponse = {
        clients: state.clients,
        totalCount: state.clients.length,
        page: 1,
        pageSize: 20,
        tabCounts: state.tabCounts,
        filterCounts: {},
        planCounts: state.planCounts,
        ...state.listFor?.(params),
      };
      return HttpResponse.json(body);
    }),
    http.get(apiUrl('/trainer/clients/pending'), () => HttpResponse.json({ rows: state.pending })),
    http.get(apiUrl('/users/me/subscription'), () => HttpResponse.json(subscription)),
    http.get(apiUrl('/payments/packages'), () => HttpResponse.json({ items: state.packages })),
    http.get(apiUrl('/payments/discounts'), () => HttpResponse.json({ items: state.discounts })),
    http.get(apiUrl('/trainer/questionnaires'), () => HttpResponse.json({ questionnaires: state.forms })),
    http.get(apiUrl('/trainer/client-tags'), () => HttpResponse.json({ tags: state.tags })),
    // The backend refuses a second tag of the same name with a 409.
    http.post(apiUrl('/trainer/client-tags'), async ({ request }) => {
      const body = (await request.json()) as { name: string; colorHex: string; description?: string };
      if (state.tags.some((tag) => tag.name === body.name)) {
        return problem(409, 'CLIENT_TAG_NAME_ALREADY_EXISTS');
      }
      const tag: ClientTagDto = {
        tagId: `cccccccc-0000-0000-0000-${String(nextTagNumber++).padStart(12, '0')}`,
        name: body.name,
        colorHex: body.colorHex,
        description: body.description,
      };
      state.tags = [...state.tags, tag];
      return HttpResponse.json(tag, { status: 201 });
    }),
    http.put(apiUrl('/trainer/clients/:clientId/tags'), async ({ request, params }) => {
      const { tagIds } = (await request.json()) as { tagIds: string[] };
      const tags = state.tags.filter((tag) => tag.tagId && tagIds.includes(tag.tagId));
      state.clients = state.clients.map((client) =>
        client.publicId === params['clientId']
          ? { ...client, tags: tags.map(({ tagId, name, colorHex }) => ({ tagId, name, colorHex })) }
          : client,
      );
      return HttpResponse.json({ clientId: String(params['clientId']), tags });
    }),
    http.get(apiUrl('/trainer/pending-invites/slot-check'), ({ request }) => {
      state.slotChecks.push(new URL(request.url).searchParams.get('email') ?? '');
      return typeof state.slotCheck === 'number'
        ? HttpResponse.json({}, { status: state.slotCheck, headers: { 'Content-Type': 'application/problem+json' } })
        : HttpResponse.json(state.slotCheck);
    }),
    http.post(apiUrl('/trainer/pending-invites'), async ({ request }) => {
      state.invites.push((await request.json()) as Record<string, unknown>);
      return HttpResponse.json({ publicId: 'dddddddd-0000-0000-0000-000000000001', email: 'x', sentAt: '2026-01-01T00:00:00Z' }, { status: 201 });
    }),
  );
}

interface RenderClientsPageOptions {
  /** Path and query to open; defaults to the Active tab. */
  url?: string;
  /** Roles of the signed-in coach; two roles make the invite drawer ask for a scope. */
  roles?: string[];
}

/** Mounts ClientsPage on `/clients` with the toast outlet the app shell provides. */
export function renderClientsPage({ url = '/clients', roles = ['Trainer'] }: RenderClientsPageOptions = {}) {
  useToastStore.setState({ toasts: [] });
  const routes: RouteObject[] = [
    {
      element: (
        <>
          <Outlet />
          <Toaster />
        </>
      ),
      children: [{ path: '/clients', element: <ClientsPage /> }],
    },
  ];
  return renderWithProviders(routes, { initialEntries: [url], auth: { roles } });
}
