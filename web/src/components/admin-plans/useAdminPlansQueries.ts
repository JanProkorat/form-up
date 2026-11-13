import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  adminPlansKey,
  createAdminPlan,
  deactivateAdminPlan,
  getAdminPlans,
  updateAdminPlan,
  type CreateSubscriptionPlanRequest,
  type UpdateSubscriptionPlanBody,
} from '@/api/admin-subscription-plans';

/** Every plan, inactive ones included. Errors are handled by the page (retry). */
export function useAdminPlans() {
  return useQuery({ queryKey: adminPlansKey, queryFn: getAdminPlans, retry: false });
}

/** The list refetches after every settled write, so a failed write also shows the server's truth. */
function useRefetchPlans() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: adminPlansKey });
}

export function useCreatePlan() {
  const refetch = useRefetchPlans();
  return useMutation({
    mutationFn: (request: CreateSubscriptionPlanRequest) => createAdminPlan(request),
    onSettled: () => refetch(),
  });
}

export function useUpdatePlan() {
  const refetch = useRefetchPlans();
  return useMutation({
    mutationFn: (variables: { code: string; body: UpdateSubscriptionPlanBody }) =>
      updateAdminPlan(variables.code, variables.body),
    onSettled: () => refetch(),
  });
}

export function useStopOfferingPlan() {
  const refetch = useRefetchPlans();
  return useMutation({
    mutationFn: (code: string) => deactivateAdminPlan(code),
    onSettled: () => refetch(),
  });
}
