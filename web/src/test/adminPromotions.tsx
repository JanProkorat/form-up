/**
 * Shared harness for the admin promotions tests: renders the real AdminPromotionsPage and serves its list and
 * create calls from one mutable `AdminPromotionsState`. The create bodies are recorded so a test can assert on
 * what the drawer sends.
 */
import { Outlet } from 'react-router-dom';
import { PromotionDuration, PromotionKind, PromotionMode, PromotionStatus } from '@/api/generated';
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
  /** The `status` query value of every list read, in order. */
  listStatuses: string[];
  /** Every POST to /admin/promotions/{id}/end: its path, content type and raw body, in order. */
  ends: { path: string; contentType: string | null; body: string }[];
  /** Answers an end request instead of ending the promotion; null ends it. */
  endResponse: (() => Response) | null;
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
  return {
    promotions,
    plans: defaultPromotionPlans(),
    plansFail: false,
    creates: [],
    listStatuses: [],
    ends: [],
    endResponse: null,
  };
}

/** Serves the page's own requests from `state`; a test adds `server.use(...)` to answer a save differently. */
export function serveAdminPromotions(state: AdminPromotionsState): void {
  server.use(
    http.get(apiUrl('/admin/promotions'), ({ request }) => {
      const status = new URL(request.url).searchParams.get('status') ?? 'All';
      state.listStatuses.push(status);
      const items = status === 'All' ? state.promotions : state.promotions.filter((item) => item.status === status);
      return HttpResponse.json({ items });
    }),
    http.post(apiUrl('/admin/promotions/:id/end'), async ({ request, params }) => {
      state.ends.push({
        path: new URL(request.url).pathname,
        contentType: request.headers.get('content-type'),
        body: await request.text(),
      });
      if (state.endResponse) return state.endResponse();
      const target = state.promotions.find((item) => item.promotionId === params['id']);
      if (!target) return new HttpResponse(null, { status: 404 });
      target.status = PromotionStatus.Ended;
      return HttpResponse.json(target);
    }),
    http.get(apiUrl('/admin/subscription-plans'), () =>
      state.plansFail ? HttpResponse.json({}, { status: 500 }) : HttpResponse.json({ plans: state.plans }),
    ),
    http.post(apiUrl('/admin/promotions'), async ({ request }) => {
      const body = (await request.json()) as Record<string, unknown>;
      state.creates.push(body);
      const created: PromotionDto = {
        promotionId: 'promo-1',
        name: String(body['name']),
        kind: body['kind'] as PromotionKind,
        value: Number(body['value']),
        mode: body['mode'] as PromotionMode,
        duration: body['duration'] as PromotionDuration,
        claimFrom: String(body['claimFrom']),
        planCodes: body['planCodes'] as string[],
        usedCount: 0,
        status: PromotionStatus.Active,
      };
      // The list refetch after a save shows the new row.
      state.promotions = [created, ...state.promotions];
      return HttpResponse.json(created, { status: 201 });
    }),
  );
}

/** Mounts the promotions page on `/admin/promotion-offers` (plus an optional query string) with the toast outlet the app shell provides. */
export function renderAdminPromotions(search = '') {
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
    { initialEntries: [`/admin/promotion-offers${search}`], auth: { roles: ['Admin'] } },
  );
}
