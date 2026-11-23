/**
 * Shared harness for the admin Overview page tests: payload builders typed with the generated response types, msw
 * handlers for the attention block and the Last 30 days report, and a render of the real AdminOverviewPage next to
 * stand-ins for the pages it links to (a test reads the opened location from `router.state.location`).
 */
import type { GetAdminAttentionResponse, GetAdminReportsResponse } from '@/api/generated';
import AdminOverviewPage from '@/pages/AdminOverviewPage';
import { adminReportsPayload } from '@/test/adminReports';
import { HttpResponse, apiUrl, http, problem } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';

/** Four attention rows: failed payments (more than shown), cancelling, gifts ending and one promotion. */
export function adminAttentionPayload(overrides: Partial<GetAdminAttentionResponse> = {}): GetAdminAttentionResponse {
  return {
    paymentFailedCount: 5,
    paymentFailedCoaches: [
      { coachId: '11111111-1111-1111-1111-111111111111', name: 'Klara Novakova' },
      { coachId: '22222222-2222-2222-2222-222222222222', name: 'Studio Forma' },
    ],
    cancellingCount: 3,
    cancellingEarliestPeriodEndsAt: '2026-11-28T10:00:00Z',
    cancellingLatestPeriodEndsAt: '2026-12-05T10:00:00Z',
    givenFreeEndingSoonCount: 4,
    promotionsEndingSoon: [
      {
        promotionId: 'promo-1',
        name: 'Black Friday',
        claimUntil: '2026-11-30T10:00:00Z',
        usedCount: 46,
        maxUses: 50,
      },
    ],
    ...overrides,
  };
}

/** Every count zero and no promotion: the all-clear state. */
export function allClearAttention(): GetAdminAttentionResponse {
  return {
    paymentFailedCount: 0,
    paymentFailedCoaches: [],
    cancellingCount: 0,
    givenFreeEndingSoonCount: 0,
    promotionsEndingSoon: [],
  };
}

/**
 * The Days30 report with a baseline on every Last 30 days figure: MRR 21 860 CZK (was 20 200), 14 sign-ups (was 11),
 * 37 paying coaches (was 35) and churn 2.7 % (was 3.1 %).
 */
export function overviewReport(overrides: Partial<GetAdminReportsResponse> = {}): GetAdminReportsResponse {
  const base = adminReportsPayload();
  return {
    ...base,
    money: {
      ...base.money,
      monthlyRevenueAfterPromotions: [{ currency: 'CZK', value: 2186000, previous: 2020000 }],
      payingCoaches: { value: 37, previous: 35 },
      churnPercent: 2.7,
      churnPercentPrevious: 3.1,
    },
    funnel: { signedUp: 14, signedUpPrevious: 11, emailVerified: 12, chosePlan: 10, startedPaidPlan: 5 },
    ...overrides,
  };
}

export interface AdminOverviewState {
  /** How many times each endpoint was fetched. */
  attentionRequests: number;
  /** The `period` query value of every report request. */
  reportPeriods: string[];
}

/** Serves both blocks; pass null for a block to answer 500 instead. */
export function serveAdminOverview(
  attention: GetAdminAttentionResponse | null = adminAttentionPayload(),
  report: GetAdminReportsResponse | null = overviewReport(),
): AdminOverviewState {
  const state: AdminOverviewState = { attentionRequests: 0, reportPeriods: [] };
  server.use(
    http.get(apiUrl('/admin/overview/attention'), () => {
      state.attentionRequests += 1;
      return attention ? HttpResponse.json(attention) : problem(500, 'ATTENTION_FAILED');
    }),
    http.get(apiUrl('/admin/reports'), ({ request }) => {
      state.reportPeriods.push(new URL(request.url).searchParams.get('period') ?? '');
      return report ? HttpResponse.json(report) : problem(500, 'REPORTS_FAILED');
    }),
  );
  return state;
}

/** Mounts the Overview on `/admin` as an admin, with stand-ins for the pages it links to. */
export function renderAdminOverview(language: 'cs' | 'en' | 'de' = 'en') {
  return renderWithProviders(
    [
      { path: '/admin', element: <AdminOverviewPage /> },
      { path: '/admin/business-reports', element: <p data-testid="reports-page" /> },
      { path: '/admin/coach-accounts', element: <p data-testid="coaches-page" /> },
      { path: '/admin/promotion-offers', element: <p data-testid="promotions-page" /> },
    ],
    { initialEntries: ['/admin'], language, auth: { roles: ['Admin'] } },
  );
}
