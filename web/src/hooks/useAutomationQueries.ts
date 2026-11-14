import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  createAutomationRule,
  createClientAutomationRule,
  deleteAutomationRule,
  getAutomationRules,
  getClientAutomations,
  overrideClientAutomation,
  pauseClientAutomation,
  resetClientAutomation,
  updateAutomationRule,
  type CreateAutomationRuleRequest,
  type CreateClientAutomationRuleRequest,
  type OverrideClientAutomationRequest,
  type PauseClientAutomationRequest,
  type UpdateAutomationRuleRequest,
} from '@/api/automations';
import { useSubscription } from '@/hooks/useSubscriptionQueries';
import { useAuthStore } from '@/stores/auth';

export const automationRulesKey = ['automations', 'rules'] as const;

/** The caller's global rules, the weekly check-in rule included. */
export function useAutomationRules() {
  const isProfessional = useAuthStore(
    (state) => state.user?.roles.some((role) => role === 'Trainer' || role === 'Nutritionist') ?? false,
  );
  // A plan without automations is refused with 403, so the request is not sent at all.
  const { isEntitled } = useSubscription();
  return useQuery({
    queryKey: automationRulesKey,
    queryFn: getAutomationRules,
    enabled: isProfessional && isEntitled('canUseAutomations'),
    retry: false,
  });
}

export const clientAutomationsKey = (clientId: string) => ['automations', 'client', clientId] as const;

/** A client's effective rules and 30-day summary. Fails with 403 when the link gives no automation coverage. */
export function useClientAutomations(clientId: string) {
  const { isEntitled } = useSubscription();
  return useQuery({
    queryKey: clientAutomationsKey(clientId),
    queryFn: () => getClientAutomations(clientId),
    enabled: Boolean(clientId) && isEntitled('canUseAutomations'),
    retry: false,
  });
}

/**
 * Create and update responses carry no summary, so every settled write refetches the global list and
 * every client's effective rules (a global change reaches them all). Callers show their own errors.
 */
function useRefetchRules() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: ['automations'] });
}

export function useCreateAutomationRule() {
  const refetch = useRefetchRules();
  return useMutation({
    mutationFn: (request: CreateAutomationRuleRequest) => createAutomationRule(request),
    onSettled: () => refetch(),
  });
}

export function useUpdateAutomationRule() {
  const refetch = useRefetchRules();
  return useMutation({
    mutationFn: (variables: { ruleId: string; request: UpdateAutomationRuleRequest }) =>
      updateAutomationRule(variables.ruleId, variables.request),
    onSettled: () => refetch(),
  });
}

export function useDeleteAutomationRule() {
  const refetch = useRefetchRules();
  return useMutation({
    mutationFn: (ruleId: string) => deleteAutomationRule(ruleId),
    onSettled: () => refetch(),
  });
}

export function useCreateClientAutomationRule(clientId: string) {
  const refetch = useRefetchRules();
  return useMutation({
    mutationFn: (request: CreateClientAutomationRuleRequest) => createClientAutomationRule(clientId, request),
    onSettled: () => refetch(),
  });
}

export function useOverrideClientAutomation(clientId: string) {
  const refetch = useRefetchRules();
  return useMutation({
    mutationFn: (variables: { ruleId: string; request: OverrideClientAutomationRequest }) =>
      overrideClientAutomation(clientId, variables.ruleId, variables.request),
    onSettled: () => refetch(),
  });
}

export function usePauseClientAutomation(clientId: string) {
  const refetch = useRefetchRules();
  return useMutation({
    mutationFn: (variables: { ruleId: string; request: PauseClientAutomationRequest }) =>
      pauseClientAutomation(clientId, variables.ruleId, variables.request),
    onSettled: () => refetch(),
  });
}

export function useResetClientAutomation(clientId: string) {
  const refetch = useRefetchRules();
  return useMutation({
    mutationFn: (ruleId: string) => resetClientAutomation(clientId, ruleId),
    onSettled: () => refetch(),
  });
}
