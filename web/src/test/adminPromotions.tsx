/**
 * Shared harness for the admin promotions tests: renders the real AdminPromotionsPage and serves its list and
 * create calls from one mutable `AdminPromotionsState`. The create bodies are recorded so a test can assert on
 * what the drawer sends.
 */
import { Outlet } from 'react-router-dom';
import type { PromotionDto } from '@/api/generated';
import { Toaster } from '@/components/ui/toast';
import AdminPromotionsPage from '@/pages/AdminPromotionsPage';
import { useToastStore } from '@/stores/toast';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';

export interface AdminPromotionsState {
  promotions: PromotionDto[];
  /** The bodies POSTed to /admin/promotions, in order. */
  creates: Record<string, unknown>[];
}

export function newAdminPromotionsState(promotions: PromotionDto[] = []): AdminPromotionsState {
  return { promotions, creates: [] };
}

/** Serves the page's own requests from `state`; a test adds `server.use(...)` to answer a save differently. */
export function serveAdminPromotions(state: AdminPromotionsState): void {
  server.use(
    http.get(apiUrl('/admin/promotions'), () => HttpResponse.json({ items: state.promotions })),
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
