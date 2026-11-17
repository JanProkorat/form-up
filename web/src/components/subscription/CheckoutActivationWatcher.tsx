import { useEffect, useRef } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { getMyProfile, profileKeys } from '@/api/profile';
import { getMySubscription, subscriptionKeys } from '@/api/subscription';
import {
  isCheckoutSettled,
  isPlanChangeSettled,
  isSecondRoleAddOnSettled,
} from '@/components/subscription/planPricing';
import { planNameFor } from '@/hooks/useSubscriptionQueries';
import { useAuthStore } from '@/stores/auth';
import {
  CHECKOUT_WATCH_SLOW_PHASE_MS,
  checkoutWatchInterval,
  useCheckoutActivationStore,
} from '@/stores/checkoutActivation';
import { useToastStore } from '@/stores/toast';

const COACH_ROLES = new Set(['trainer', 'nutritionist']);

/** Read fresh on every call so the fast-to-slow switch and the end of polling need no re-render. */
function watchRefetchInterval(): number | false {
  const { startedAt } = useCheckoutActivationStore.getState();
  return startedAt === null ? false : checkoutWatchInterval(startedAt, Date.now());
}

/**
 * Keeps looking for a checkout's plan after the return page gave up waiting, for the plan of an upgrade that
 * awaits payment on Stripe's page, or for a second-role add-on that does. Polls every few seconds at first, then every 30 s, then only refreshes on window
 * focus, until the plan lands or the coach signs out. An upgrade or add-on watch also ends with a message when the window
 * closes without the plan landing. The watch survives a reload of the tab and belongs to the coach who started it.
 * Bounded polling is the agreed exception to the no-polling rule: no push event announces a webhook landing.
 */
export function CheckoutActivationWatcher() {
  const { i18n } = useTranslation();
  const isCoach = useAuthStore((s) => s.user?.roles.some((role) => COACH_ROLES.has(role.toLowerCase())) ?? false);
  const currentUserId = useAuthStore((s) => s.user?.publicId ?? null);
  const startedAt = useCheckoutActivationStore((s) => s.startedAt);
  const ownerId = useCheckoutActivationStore((s) => s.userId);
  const targetPlanCode = useCheckoutActivationStore((s) => s.targetPlanCode);
  const awaitsSecondRoleAddOn = useCheckoutActivationStore((s) => s.awaitsSecondRoleAddOn);
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
  const refetchSubscription = subscription.refetch;
  const dataRef = useRef(data);
  useEffect(() => {
    dataRef.current = data;
  }, [data]);

  useEffect(() => {
    // Read the store rather than the closure so a repeated effect run cannot announce the plan twice.
    const state = useCheckoutActivationStore.getState();
    if (state.startedAt === null) return;
    if (state.awaitsSecondRoleAddOn) {
      // The role is not added here: the coach adds it in Settings, where a failure has a dialog to show it.
      if (!isSecondRoleAddOnSettled(data)) return;
      state.stop();
      useToastStore.getState().addToast(i18n.t('subscription.billing.addRole.paymentConfirmed'), 'success');
      return;
    }
    const target = state.targetPlanCode;
    if (!(target ? isPlanChangeSettled(data, target) : isCheckoutSettled(data))) return;
    state.stop();
    useToastStore
      .getState()
      .addToast(
        i18n.t(target ? 'subscription.billing.changed.now' : 'subscription.billing.returning.activated', {
          plan: planNameFor(data, i18n.language),
        }),
        'success',
      );
  }, [data, i18n]);

  // An upgrade watch has an end: when the window closes without the plan landing, the payment was not confirmed and
  // the coach stays on the old plan. One last read first, so a late landing is not reported as a failure.
  useEffect(() => {
    if (!watching || startedAt === null || (targetPlanCode === null && !awaitsSecondRoleAddOn)) return;
    const timer = setTimeout(
      () => {
        void (async () => {
          const latest = (await refetchSubscription()).data ?? dataRef.current;
          const state = useCheckoutActivationStore.getState();
          if (
            state.startedAt !== startedAt ||
            state.targetPlanCode !== targetPlanCode ||
            state.awaitsSecondRoleAddOn !== awaitsSecondRoleAddOn
          ) {
            return;
          }
          state.stop();
          if (targetPlanCode === null) {
            const key = isSecondRoleAddOnSettled(latest)
              ? 'subscription.billing.addRole.paymentConfirmed'
              : 'subscription.billing.addRole.notConfirmed';
            useToastStore.getState().addToast(i18n.t(key), isSecondRoleAddOnSettled(latest) ? 'success' : 'error');
            return;
          }
          const plan = planNameFor(latest, i18n.language);
          if (isPlanChangeSettled(latest, targetPlanCode)) {
            useToastStore.getState().addToast(i18n.t('subscription.billing.changed.now', { plan }), 'success');
            return;
          }
          useToastStore.getState().addToast(i18n.t('subscription.billing.upgradePayment.notConfirmed', { plan }), 'error');
        })();
      },
      Math.max(0, startedAt + CHECKOUT_WATCH_SLOW_PHASE_MS - Date.now()),
    );
    return () => clearTimeout(timer);
  }, [watching, startedAt, targetPlanCode, awaitsSecondRoleAddOn, refetchSubscription, i18n]);

  return null;
}
