import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  adminCoachDetailKey,
  adminCoachesKey,
  adminCoachesListKey,
  cancelCoachRenewal,
  disableCoachAccount,
  getAdminCoach,
  getAdminCoaches,
  giveCoachPlanFree,
  type AdminCoachesQuery,
  type GiveCoachPlanFreeRequest,
} from '@/api/admin-coaches';

/** One page of coaches plus stats. The previous page stays visible while the next one loads. */
export function useAdminCoaches(query: AdminCoachesQuery) {
  return useQuery({
    queryKey: [...adminCoachesListKey, query.page, query.status, query.search],
    queryFn: ({ signal }) => getAdminCoaches(query, signal),
    placeholderData: keepPreviousData,
    retry: false,
  });
}

/** The coach shown in the drawer; idle until a coach is picked. */
export function useAdminCoach(coachId: string | null) {
  return useQuery({
    queryKey: adminCoachDetailKey(coachId ?? ''),
    queryFn: ({ signal }) => getAdminCoach(coachId ?? '', signal),
    enabled: coachId !== null,
    retry: false,
  });
}

/** Refetches the list (rows and stats) and every open coach detail after any write, settled or not. */
function useRefetchCoaches() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: adminCoachesKey });
}

export function useGiveCoachPlanFree(coachId: string) {
  const refetch = useRefetchCoaches();
  return useMutation({
    mutationFn: (request: GiveCoachPlanFreeRequest) => giveCoachPlanFree(coachId, request),
    onSettled: () => refetch(),
  });
}

export function useCancelCoachRenewal(coachId: string) {
  const refetch = useRefetchCoaches();
  return useMutation({
    mutationFn: (reason: string) => cancelCoachRenewal(coachId, reason),
    onSettled: () => refetch(),
  });
}

export function useDisableCoachAccount(coachId: string) {
  const refetch = useRefetchCoaches();
  return useMutation({
    mutationFn: (reason: string) => disableCoachAccount(coachId, reason),
    onSettled: () => refetch(),
  });
}
