/**
 * Shared harness for the admin promotions tests: renders the real AdminPromotionsPage and serves its list and
 * create calls from one mutable `AdminPromotionsState`. The create bodies are recorded so a test can assert on
 * what the drawer sends.
 */
import { Outlet } from 'react-router-dom';
import type { PromotionDto, SubscriptionPlanDto } from '@/api/generated';
import { Toaster } from '@/components/ui/toast';
import AdminPromotionsPage from '@/pages/AdminPromotionsPage';
import { useToastStore } from '@/stores/toast';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { freePlan, paidPlan } from '@/test/adminPlans';
import { renderWithProviders } from '@/test/render';

export interface AdminPromotionsState {
  promotions: PromotionDto[];
  /** What GET /admin/subscription-plans returns. */
  plans: SubscriptionPlanDto[];
  /** While true, GET /admin/subscription-plans answers 500. */
  plansFail: boolean;
  /** The bodies POSTed to /admin/promotions, in order. */
  creates: Record<string, unknown>[];
}

/** The default catalog: Free plus the three launch plans, cheapest first. */
export function defaultPromotionPlans(): SubscriptionPlanDto[] {
  return [
    freePlan(),
    paidPlan({ code: 'start', nameCs: 'Start', nameEn: 'Start', nameDe: 'Start', priceMinorUnits: 29900 }),
    paidPlan({ code: 'pro', priceMinorUnits: 64900 }),
    paidPlan({ code: 'studio', nameCs: 'Studio', nameEn: 'Studio', nameDe: 'Studio', priceMinorUnits: 99900 }),
  ];
}

export function newAdminPromotionsState(promotions: PromotionDto[] = []): AdminPromotionsState {
  return { promotions, plans: defaultPromotionPlans(), plansFail: false, creates: [] };
}

/** Serves the page's own requests from `state`; a test adds `server.use(...)` to answer a save differently. */
export function serveAdminPromotions(state: AdminPromotionsState): void {
  server.use(
    http.get(apiUrl('/admin/promotions'), () => HttpResponse.json({ items: state.promotions })),
    http.get(apiUrl('/admin/subscription-plans'), () =>
      state.plansFail ? HttpResponse.json({}, { status: 500 }) : HttpResponse.json({ plans: state.plans }),
    ),
    http.post(apiUrl('/admin/promotions'), async ({ request }) => {
      const body = (await request.json()) as Record<string, unknown>;
      state.creates.push(body);
      return HttpResponse.json({ promotionId: 'promo-1', name: body['name'] }, { status: 201 });
    }),
  );
}

/** Mounts the promotions page on `/admin/promotion-offers` with the toast outlet the app shell provides. */
export function renderAdminPromotions() {
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
        children: [{ path: '/admin/promotion-offers', element: <AdminPromotionsPage /> }],
      },
    ],
    { initialEntries: ['/admin/promotion-offers'], auth: { roles: ['Admin'] } },
  );
}
