/**
 * Shared harness for the client-detail tests: renders the real ClientDetailPage on its route and serves
 * the requests the page makes from one mutable `ClientDetailState`. A test changes the state (or adds its own
 * `server.use(...)` handler, which wins over these) and then asserts on what the coach sees.
 * Payloads are typed with the generated API types, so a changed contract breaks the typecheck.
 */
import { Outlet } from 'react-router-dom';
import type { RouteObject } from 'react-router-dom';
import type {
  ClientPlanItem,
  GetClientDashboardResponse,
  GetClientCheckInHistoryResponse,
  GetClientFormsResponse,
  GetTrainerClientPhotosResponse,
  GetMeasurementsResponse,
  GetMySubscriptionResponse,
  GetPlanResponse,
  ListClientPlansResponse,
} from '@/api/generated';
import { Toaster } from '@/components/ui/toast';
import ClientDetailPage from '@/pages/ClientDetailPage';
import { useToastStore } from '@/stores/toast';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';

export const CLIENT_ID = '11111111-1111-1111-1111-111111111111';
export const CLIENT_USER_ID = '22222222-2222-2222-2222-222222222222';
export const COACH_USER_ID = '33333333-3333-3333-3333-333333333333';

export interface ClientDetailState {
  dashboard: GetClientDashboardResponse;
  plans: ClientPlanItem[];
  measurements: GetMeasurementsResponse;
}

/** A nutritionist-only link to the client (the seeded QA link in the E2E harness). */
export function clientDashboard(overrides: GetClientDashboardResponse = {}): GetClientDashboardResponse {
  return {
    clientPublicId: CLIENT_ID,
    clientUserId: CLIENT_USER_ID,
    firstName: 'QA',
    lastName: 'Client',
    isActive: true,
    canViewNutritionPlans: true,
    canViewTrainingPlans: false,
    ...overrides,
  };
}

export function newClientDetailState(overrides: Partial<ClientDetailState> = {}): ClientDetailState {
  return {
    dashboard: clientDashboard(),
    plans: [],
    measurements: { items: [], totalCount: 0, page: 1, pageSize: 50 },
    ...overrides,
  };
}

/** A plan as GET /nutrition/plans/{id} returns it; `weeks` and `mealLogs` are the parts tests care about. */
export function nutritionPlan(planId: string, overrides: GetPlanResponse = {}): GetPlanResponse {
  return {
    planId,
    clientId: CLIENT_ID,
    nutritionistId: COACH_USER_ID,
    name: 'Plan',
    status: 'Active',
    globalSettings: {},
    weeks: [],
    version: 1,
    dateCreated: '2026-01-01T00:00:00Z',
    mealLogs: [],
    supplements: [],
    ...overrides,
  };
}

/** Answers GET /nutrition/plans/{planId}; `plan` is read per request, so a test can swap it. */
export function servePlan(planId: string, plan: () => GetPlanResponse | number): void {
  server.use(
    http.get(apiUrl(`/nutrition/plans/${planId}`), () => {
      const result = plan();
      return typeof result === 'number'
        ? HttpResponse.json({ status: result }, { status: result, headers: { 'Content-Type': 'application/problem+json' } })
        : HttpResponse.json(result);
    }),
  );
}

/** Lists a plan and serves its detail (the two requests the tab makes for an Active or Draft plan). */
export function addPlan(state: ClientDetailState, item: ClientPlanItem, detail?: () => GetPlanResponse | number): void {
  state.plans = [...state.plans, { planType: 'Nutrition', ...item }];
  if (item.planId && detail) {
    servePlan(item.planId, detail);
  }
}

const subscription: GetMySubscriptionResponse = { features: {} };
const noForms: GetClientFormsResponse = {};
const noPhotos: GetTrainerClientPhotosResponse = { photos: [] };
const noCheckIns: GetClientCheckInHistoryResponse = { weeks: [], answeredCount: 0, totalCount: 0 };

/** Serves the page's own requests from `state`; everything else is declared by the test. */
export function serveClientDetail(state: ClientDetailState): void {
  server.use(
    http.get(apiUrl(`/trainer/clients/${CLIENT_ID}`), () => HttpResponse.json(state.dashboard)),
    http.get(apiUrl(`/trainer/clients/${CLIENT_ID}/plans`), () => {
      const body: ListClientPlansResponse = {
        plans: state.plans,
        canViewNutritionPlans: state.dashboard.canViewNutritionPlans,
        canViewTrainingPlans: state.dashboard.canViewTrainingPlans,
      };
      return HttpResponse.json(body);
    }),
    http.get(apiUrl(`/trainer/clients/${CLIENT_ID}/measurements`), () => HttpResponse.json(state.measurements)),
    http.get(apiUrl('/users/me/subscription'), () => HttpResponse.json(subscription)),
    http.get(apiUrl(`/trainer/clients/${CLIENT_ID}/forms`), () => HttpResponse.json(noForms)),
    http.get(apiUrl(`/trainer/clients/${CLIENT_ID}/photos`), () => HttpResponse.json(noPhotos)),
    http.get(apiUrl(`/trainer/clients/${CLIENT_USER_ID}/weekly-check-ins/history`), () => HttpResponse.json(noCheckIns)),
  );
}

/** Mounts ClientDetailPage on the Nutrition tab; the plan editor and template library are stand-ins. */
export function renderNutritionTab() {
  useToastStore.setState({ toasts: [] });
  const routes: RouteObject[] = [
    {
      element: (
        <>
          <Outlet />
          <Toaster />
        </>
      ),
      children: [
        { path: '/clients/:clientId/nutrition/plans/:planId', element: <div data-testid="plan-editor" /> },
        { path: '/clients/:clientId/*', element: <ClientDetailPage /> },
        { path: '/plan-templates', element: <div data-testid="template-library" /> },
      ],
    },
  ];
  return renderWithProviders(routes, {
    initialEntries: [`/clients/${CLIENT_ID}/nutrition`],
    auth: { roles: ['Nutritionist'] },
  });
}
