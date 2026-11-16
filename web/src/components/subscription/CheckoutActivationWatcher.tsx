import { useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { getMyProfile, profileKeys } from '@/api/profile';
import { getMySubscription, subscriptionKeys } from '@/api/subscription';
import { isCheckoutSettled } from '@/components/subscription/planPricing';
import { planNameFor } from '@/hooks/useSubscriptionQueries';
import { useAuthStore } from '@/stores/auth';
import { useCheckoutActivationStore } from '@/stores/checkoutActivation';
import { useToastStore } from '@/stores/toast';

const COACH_ROLES = new Set(['trainer', 'nutritionist']);
const WATCH_POLL_INTERVAL_MS = 5000;

/**
 * Keeps looking for a checkout's plan after the return page gave up waiting. Polls every few seconds until the
 * watch window ends, then only refreshes on window focus, until the plan lands or the coach signs out. Bounded
 * polling is the agreed exception to the no-polling rule: no push event announces a webhook landing.
 */
export function CheckoutActivationWatcher() {
  const { i18n } = useTranslation();
  const isCoach = useAuthStore((s) => s.user?.roles.some((role) => COACH_ROLES.has(role.toLowerCase())) ?? false);
  const watchUntil = useCheckoutActivationStore((s) => s.watchUntil);
  const watching = isCoach && watchUntil !== null;

  const refetchInterval = () => (watchUntil !== null && Date.now() < watchUntil ? WATCH_POLL_INTERVAL_MS : false);

  const subscription = useQuery({
    queryKey: subscriptionKeys.mine,
    queryFn: getMySubscription,
    enabled: watching,
    refetchInterval,
    // The global staleTime would skip a focus refetch of fresh data; the watch needs every focus to ask again.
    refetchOnWindowFocus: 'always',
  });
  useQuery({
    queryKey: profileKeys.me,
    queryFn: getMyProfile,
    enabled: watching,
    refetchInterval,
    refetchOnWindowFocus: 'always',
  });

  const data = subscription.data;
  useEffect(() => {
    // Read the store rather than the closure so a repeated effect run cannot announce the plan twice.
    if (useCheckoutActivationStore.getState().watchUntil === null) return;
    if (!isCheckoutSettled(data)) return;
    useCheckoutActivationStore.getState().stop();
    useToastStore
      .getState()
      .addToast(i18n.t('subscription.billing.returning.activated', { plan: planNameFor(data, i18n.language) }), 'success');
  }, [data, i18n]);

  return null;
}
