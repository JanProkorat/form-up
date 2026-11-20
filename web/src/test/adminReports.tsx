/**
 * Shared harness for the admin Reports page tests: payload builders typed with the generated response types, an
 * msw handler that serves one payload per `period` query value, and a render of the real AdminReportsPage.
 */
import { AdminReportPeriod, type GetAdminReportsResponse } from '@/api/generated';
import AdminReportsPage from '@/pages/AdminReportsPage';
import { HttpResponse, apiUrl, http, problem } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';

/** A 30-day payload with two currencies on the money side, a baseline and a usage block. */
export function adminReportsPayload(overrides: Partial<GetAdminReportsResponse> = {}): GetAdminReportsResponse {
  return {
    period: AdminReportPeriod.Days30,
    snapshotDay: '2026-11-15',
    comparedFrom: undefined,
    money: {
      monthlyRevenueFull: [{ currency: 'CZK', value: 2648000, previous: 2400000 }],
      monthlyRevenueAfterPromotions: [
        { currency: 'CZK', value: 2186000, previous: 2020000 },
        { currency: 'EUR', value: 15000, previous: undefined },
      ],
      averagePerPayingCoach: [{ currency: 'CZK', value: 59100, previous: 57900, coachesBilled: 37 }],
      collectedThisMonth: [{ currency: 'CZK', value: 1994000, previous: 1872000 }],
      lostToCancellations: {
        count: { value: 3, previous: 2 },
        amounts: [{ currency: 'CZK', value: 119600 }],
      },
      churnPercent: 7.5,
      plans: [
        {
          planCode: 'start',
          nameCs: 'Start CS',
          nameEn: 'Start',
          nameDe: 'Start DE',
          payingCoaches: 18,
          revenue: [{ currency: 'CZK', amountMinorUnits: 538200 }],
        },
        {
          planCode: 'pro',
          nameCs: 'Pro CS',
          nameEn: 'Pro',
          nameDe: 'Pro DE',
          payingCoaches: 13,
          revenue: [{ currency: 'CZK', amountMinorUnits: 778700 }],
        },
      ],
      addOns: [{ currency: 'CZK', count: 9, revenueMinorUnits: 269700 }],
    },
    funnel: { signedUp: 64, emailVerified: 58, chosePlan: 51, startedPaidPlan: 9 },
    planChanges: {
      upgraded: { count: 4, revenueEffect: [{ currency: 'CZK', amountMinorUnits: 119600 }] },
      downgraded: { count: 1, revenueEffect: [{ currency: 'CZK', amountMinorUnits: -30000 }] },
      cancelled: { count: 3, revenueEffect: [{ currency: 'CZK', amountMinorUnits: -119600 }] },
      paymentFailedCoaches: 2,
      promotionCodesApplied: 6,
    },
    mrrSeries: [
      {
        day: '2026-11-01',
        full: [{ currency: 'CZK', amountMinorUnits: 2400000 }],
        afterPromotions: [{ currency: 'CZK', amountMinorUnits: 2020000 }],
      },
      {
        day: '2026-11-15',
        full: [{ currency: 'CZK', amountMinorUnits: 2648000 }],
        afterPromotions: [{ currency: 'CZK', amountMinorUnits: 2186000 }],
      },
    ],
    usage: {
      totalCoaches: 87,
      activeCoachesLast30Days: 61,
      activeClientsLast7Days: { value: 412, previous: 392 },
      planWeeksPublishedLast7Days: 138,
      freeCoachesAtClientCap: 12,
      medianClientsPerCoach: 6,
    },
    ...overrides,
  };
}

export interface AdminReportsState {
  /** The `period` query value of every request, in order. */
  requests: string[];
}

/** Serves the page from `payloads` by period; a period without a payload answers 500. */
export function serveAdminReports(
  payloads: Partial<Record<AdminReportPeriod, GetAdminReportsResponse>>,
): AdminReportsState {
  const state: AdminReportsState = { requests: [] };
  server.use(
    http.get(apiUrl('/admin/reports'), ({ request }) => {
      const period = new URL(request.url).searchParams.get('period') ?? '';
      state.requests.push(period);
      const payload = payloads[period as AdminReportPeriod];
      return payload ? HttpResponse.json(payload) : problem(500, 'REPORTS_FAILED');
    }),
  );
  return state;
}

/** Mounts the Reports page on `/admin/business-reports` in the given UI language. */
export function renderAdminReports(language: 'cs' | 'en' | 'de' = 'en') {
  return renderWithProviders([{ path: '/admin/business-reports', element: <AdminReportsPage /> }], {
    initialEntries: ['/admin/business-reports'],
    language,
    auth: { roles: ['Admin'] },
  });
}
