import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/api/client';
import { AdminReportPeriod } from '@/api/generated';

export const adminReportsKey = ['admin', 'reports'] as const;

/** The totals for one look-back window; each period is its own cache entry. */
export function useAdminReports(period: AdminReportPeriod) {
  return useQuery({
    queryKey: [...adminReportsKey, period],
    queryFn: ({ signal }) => apiClient.getAdminReportsEndpoint(period, signal),
    retry: false,
  });
}
