import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  getTrainerCheckIns,
  getClientCheckInHistory,
  markCheckInReviewed,
} from '@/api/weekly-checkins';

// ── Query key factories ──────────────────────────────────────────────────────

export const weeklyCheckInKeys = {
  /** All trainer weekly-check-in queries */
  all: ['weekly-check-ins'] as const,
  /**
   * List for the Forms → Check-ins page (CheckInsPage).
   * Omit `weekStartDate` for the active (week-agnostic) set; pass an ISO Monday for one week.
   */
  trainerList: (weekStartDate?: string) =>
    ['weekly-check-ins', 'trainer-list', weekStartDate ?? 'active'] as const,
  /** Weekly history slots for a specific client (ApplicationUser Id) */
  clientHistory: (clientUserId: string, weeks: number) =>
    ['weekly-check-ins', 'client-history', clientUserId, weeks] as const,
} as const;

// ── Hooks ────────────────────────────────────────────────────────────────────

/**
 * The trainer's clients' weekly check-ins (CheckInsPage); `data` is the list of rows.
 *
 * @param weekStartDate - Optional ISO Monday (YYYY-MM-DD); omit for the week-agnostic active set.
 * @param options.enabled - Set false to skip the request (e.g. behind the paywall). Defaults to true.
 */
export function useTrainerWeeklyCheckIns(weekStartDate?: string, options: { enabled?: boolean } = {}) {
  return useQuery({
    enabled: options.enabled ?? true,
    queryKey: weeklyCheckInKeys.trainerList(weekStartDate),
    queryFn: () => getTrainerCheckIns(weekStartDate),
    staleTime: 60_000,
    select: (response) => response.checkIns ?? [],
  });
}

/**
 * Weekly check-in history (oldest first) for the client-detail Overview trend.
 *
 * @param clientUserId - Client's ApplicationUser Id (Guid string), not the route PublicId.
 * @param weeks        - Number of weekly slots to load.
 */
export function useClientCheckInHistory(clientUserId: string, weeks: number) {
  return useQuery({
    queryKey: weeklyCheckInKeys.clientHistory(clientUserId, weeks),
    queryFn: () => getClientCheckInHistory(clientUserId, weeks),
    staleTime: 60_000,
    enabled: Boolean(clientUserId),
    retry: false,
  });
}

/**
 * Mutation for POST /trainer/weekly-check-ins/{id}/mark-reviewed.
 * On success, invalidates both the trainer list and client-current queries
 * so banners update without a page reload.
 */
export function useMarkCheckInReviewed() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (id: string) => markCheckInReviewed(id),
    onSuccess: () => {
      // Invalidate all weekly check-in queries (trainer list + all client-currents)
      void queryClient.invalidateQueries({ queryKey: weeklyCheckInKeys.all });
    },
  });
}
