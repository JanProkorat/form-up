import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  chooseFreePlan,
  getCoachPlans,
  getKeepClients,
  getMySubscription,
  setKeepClients,
  subscriptionKeys,
} from '@/api/subscription';
import type { GetMySubscriptionResponse, SubscriptionFeaturesDto } from '@/api/generated';
import { useAuthStore } from '@/stores/auth';

const COACH_ROLES = new Set(['trainer', 'nutritionist']);

export type SubscriptionFeature = keyof SubscriptionFeaturesDto;

/** Plan name in the given UI language, falling back to English, Czech, then the plan code. */
export function planNameFor(subscription: GetMySubscriptionResponse | undefined, language: string): string {
  if (!subscription) return '';
  const lang = language.slice(0, 2).toLowerCase();
  const byLang = lang === 'cs' ? subscription.nameCs : lang === 'de' ? subscription.nameDe : subscription.nameEn;
  return byLang || subscription.nameEn || subscription.nameCs || subscription.nameDe || subscription.planCode || '';
}

/** Name of the plan that lapsed in the given UI language, or '' when none is known. */
export function lapsedPlanNameFor(subscription: GetMySubscriptionResponse | undefined, language: string): string {
  if (!subscription) return '';
  const lang = language.slice(0, 2).toLowerCase();
  const byLang =
    lang === 'cs' ? subscription.lapsedPlanNameCs : lang === 'de' ? subscription.lapsedPlanNameDe : subscription.lapsedPlanNameEn;
  return byLang || subscription.lapsedPlanNameEn || subscription.lapsedPlanNameCs || subscription.lapsedPlanNameDe || '';
}

/** Name of the ended admin-given plan in the given UI language, or '' when none is known. */
export function endedPlanNameFor(subscription: GetMySubscriptionResponse | undefined, language: string): string {
  if (!subscription) return '';
  const lang = language.slice(0, 2).toLowerCase();
  const byLang =
    lang === 'cs' ? subscription.endedPlanNameCs : lang === 'de' ? subscription.endedPlanNameDe : subscription.endedPlanNameEn;
  return byLang || subscription.endedPlanNameEn || subscription.endedPlanNameCs || subscription.endedPlanNameDe || '';
}

/**
 * The signed-in coach's subscription, one shared query. Disabled for anyone without a
 * Trainer/Nutritionist role (the endpoint would answer 403), so no request and no error UI there.
 */
export function useSubscription() {
  const { i18n } = useTranslation();
  const isCoach = useAuthStore((s) => s.user?.roles.some((role) => COACH_ROLES.has(role.toLowerCase())) ?? false);
  const query = useQuery({
    queryKey: subscriptionKeys.mine,
    queryFn: getMySubscription,
    enabled: isCoach,
  });
  const data = query.data;

  return {
    query,
    isCoach,
    data,
    planName: planNameFor(data, i18n.language),
    features: data?.features,
    /** True unless the loaded plan says no: unknown or failed lookups never lock the UI. */
    isEntitled: (feature: SubscriptionFeature) => data?.features?.[feature] !== false,
    /** Null means unlimited. */
    clientLimit: data?.clientLimit ?? null,
    activeClientCount: data?.activeClientCount ?? 0,
    /** True while a paid plan has lapsed and the coach is on Free. */
    isLapsed: data?.isLapsed === true,
    lapsedPlanName: lapsedPlanNameFor(data, i18n.language),
    /** True while an admin-given plan has ended and the coach has not chosen a plan yet. */
    planChoiceRequired: data?.planChoiceRequired === true,
    endedPlanName: endedPlanNameFor(data, i18n.language),
    /** Live clients above the Free plan's cap (lapsed, or on Free with too many). */
    overCapClientCount: data?.overCapClientCount ?? 0,
  };
}

/** Refetches the subscription after something that changes client count or plan. */
export function useInvalidateSubscription() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: subscriptionKeys.mine });
}

/** The coach's live clients with the ones the Free plan keeps; answers 409 unless lapsed, choosing a plan, or on Free over the cap. */
export function useKeepClients(enabled: boolean) {
  return useQuery({
    queryKey: subscriptionKeys.keepClients,
    queryFn: getKeepClients,
    enabled,
    retry: false,
  });
}

/** Saves the kept clients, then refreshes the list and the subscription (kept count, sidebar row). */
export function useSetKeepClients() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: setKeepClients,
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: subscriptionKeys.keepClients }),
        queryClient.invalidateQueries({ queryKey: subscriptionKeys.mine }),
      ]);
    },
  });
}

/** The plans a coach can choose between, cheapest first. */
export function useCoachPlans(enabled: boolean) {
  return useQuery({
    queryKey: subscriptionKeys.plans,
    queryFn: getCoachPlans,
    enabled,
  });
}

/** Takes the Free plan after a given plan ended, then refreshes the subscription and the keep-clients list. */
export function useChooseFreePlan() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: chooseFreePlan,
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: subscriptionKeys.mine }),
        queryClient.invalidateQueries({ queryKey: subscriptionKeys.keepClients }),
      ]);
    },
  });
}
