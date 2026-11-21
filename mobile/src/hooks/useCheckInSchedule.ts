import { useQuery } from '@tanstack/react-query';

import { CHECK_IN_SCHEDULE_QUERY_KEY, fetchCheckInSchedule } from '@/api/checkIns';
import { useAuthStore } from '@/stores/auth';

/** The signed-in client's check-in schedule, one entry per coach; empty until loaded or on failure. */
export function useCheckInSchedule() {
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const query = useQuery({
    queryKey: CHECK_IN_SCHEDULE_QUERY_KEY,
    queryFn: ({ signal }) => fetchCheckInSchedule(signal),
    enabled: isAuthenticated,
    retry: 1,
  });
  return { items: query.data ?? [] };
}
