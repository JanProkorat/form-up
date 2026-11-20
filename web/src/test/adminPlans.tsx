/**
 * Shared harness for the admin plan editor tests: renders the real AdminPlanEditorPage on its route and serves
 * the plan list plus the create and update calls from one mutable `AdminPlansState`. The bodies the page sends
 * are recorded, so a test can assert on exactly what a save puts on the wire.
 */
import { Outlet } from 'react-router-dom';
import { BillingInterval } from '@/api/generated';
import type { SubscriptionPlanDto } from '@/api/generated';
import { Toaster } from '@/components/ui/toast';
import AdminPlanEditorPage from '@/pages/AdminPlanEditorPage';
import { useToastStore } from '@/stores/toast';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';

/** The request bodies the editor sent, in order. */
export interface AdminPlansState {
  plans: SubscriptionPlanDto[];
  updates: { code: string; body: Record<string, unknown> }[];
  creates: Record<string, unknown>[];
}

/** A paid plan with a monthly and a yearly price and a paid second-role add-on (CZK, minor units). */
export function paidPlan(overrides: Partial<SubscriptionPlanDto> = {}): SubscriptionPlanDto {
  return {
    code: 'pro',
    nameCs: 'Pro',
    nameEn: 'Pro',
    nameDe: 'Pro',
    bothRolesIncluded: false,
    secondRoleAddOnPriceMinorUnits: 29900,
    priceMinorUnits: 64900,
    annualPriceMinorUnits: 649000,
    annualSecondRoleAddOnPriceMinorUnits: 299000,
    currency: 'CZK',
    billingInterval: BillingInterval.Monthly,
    maxActiveClients: 30,
    canMessage: true,
    canCreatePlans: true,
    isActive: true,
    coachCount: 0,
    ...overrides,
  };
}

/** The Free plan: price 0, never sold annually. */
export function freePlan(): SubscriptionPlanDto {
  return {
    code: 'free',
    nameCs: 'Free',
    nameEn: 'Free',
    nameDe: 'Free',
    bothRolesIncluded: true,
    priceMinorUnits: 0,
    currency: 'CZK',
    billingInterval: BillingInterval.Monthly,
    maxActiveClients: 3,
    canMessage: true,
    isActive: true,
    coachCount: 0,
  };
}

export function newAdminPlansState(plans: SubscriptionPlanDto[] = [paidPlan()]): AdminPlansState {
  return { plans, updates: [], creates: [] };
}

/** Serves the editor's own requests from `state`; a test adds `server.use(...)` to answer a save differently. */
export function serveAdminPlans(state: AdminPlansState): void {
  server.use(
    http.get(apiUrl('/admin/subscription-plans'), () => HttpResponse.json({ plans: state.plans })),
    http.put(apiUrl('/admin/subscription-plans/:code'), async ({ params, request }) => {
      const body = (await request.json()) as Record<string, unknown>;
      const code = String(params['code']);
      state.updates.push({ code, body });
      return HttpResponse.json(state.plans.find((plan) => plan.code === code) ?? {});
    }),
    http.post(apiUrl('/admin/subscription-plans'), async ({ request }) => {
      const body = (await request.json()) as Record<string, unknown>;
      state.creates.push(body);
      return HttpResponse.json({ code: body['code'] }, { status: 201 });
    }),
  );
}

/** Mounts the editor on `/admin/plans/:planId` (or `/admin/plans/new`), with the plan list as a stand-in. */
export function renderAdminPlanEditor(planCode: string | 'new') {
  useToastStore.setState({ toasts: [] });
  return renderWithProviders(
    [
      {
        element: (
          <>
            <Outlet />
            <Toaster />
          </>
        ),
        children: [
          { path: '/admin/plans/new', element: <AdminPlanEditorPage /> },
          { path: '/admin/plans/:planId', element: <AdminPlanEditorPage /> },
          { path: '/admin/plans', element: <div data-testid="plans-list" /> },
        ],
      },
    ],
    { initialEntries: [`/admin/plans/${planCode}`], auth: { roles: ['Admin'] } },
  );
}
