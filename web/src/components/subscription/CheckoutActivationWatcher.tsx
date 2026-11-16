import { useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { getMyProfile, profileKeys } from '@/api/profile';
import { getMySubscription, subscriptionKeys } from '@/api/subscription';
import { isCheckoutSettled } from '@/components/subscription/planPricing';
import { planNameFor } from '@/hooks/useSubscriptionQueries';
import { useAuthStore } from '@/stores/auth';
import { checkoutWatchInterval, useCheckoutActivationStore } from '@/stores/checkoutActivation';
import { useToastStore } from '@/stores/toast';

const COACH_ROLES = new Set(['trainer', 'nutritionist']);

/** Read fresh on every call so the fast-to-slow switch and the end of polling need no re-render. */
function watchRefetchInterval(): number | false {
  const { startedAt } = useCheckoutActivationStore.getState();
  return startedAt === null ? false : checkoutWatchInterval(startedAt, Date.now());
}

/**
 * Keeps looking for a checkout's plan after the return page gave up waiting. Polls every few seconds at first,
 * then every 30 s, then only refreshes on window focus, until the plan lands or the coach signs out. The watch
 * survives a reload of the tab and belongs to the coach who made the purchase. Bounded polling is the agreed
 * exception to the no-polling rule: no push event announces a webhook landing.
 */
export function CheckoutActivationWatcher() {
  const { i18n } = useTranslation();
  const isCoach = useAuthStore((s) => s.user?.roles.some((role) => COACH_ROLES.has(role.toLowerCase())) ?? false);
  const currentUserId = useAuthStore((s) => s.user?.publicId ?? null);
  const startedAt = useCheckoutActivationStore((s) => s.startedAt);
  const ownerId = useCheckoutActivationStore((s) => s.userId);
  const watching = isCoach && startedAt !== null && ownerId === currentUserId;

  // Another coach signed in on this tab: drop their watch. While the user is unknown (session restoring), wait.
  useEffect(() => {
    if (currentUserId !== null && ownerId !== null && ownerId !== currentUserId) {
      useCheckoutActivationStore.getState().stop();
    }
  }, [currentUserId, ownerId]);

  const refetchInterval = watchRefetchInterval;

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
    if (useCheckoutActivationStore.getState().startedAt === null) return;
    if (!isCheckoutSettled(data)) return;
    useCheckoutActivationStore.getState().stop();
    useToastStore
      .getState()
      .addToast(i18n.t('subscription.billing.returning.activated', { plan: planNameFor(data, i18n.language) }), 'success');
  }, [data, i18n]);

  return null;
}
