import { useCallback, useEffect, useRef } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { getMyProfile, profileKeys } from '@/api/profile';
import { getMyCoachRoles, rolesKeys, type MyCoachRoles } from '@/api/roles';
import { getMySubscription, subscriptionKeys } from '@/api/subscription';
import {
  isCheckoutSettled,
  isPlanChangeSettled,
  isSecondRoleAddOnSettled,
} from '@/components/subscription/planPricing';
import { planNameFor } from '@/hooks/useSubscriptionQueries';
import { executeRefresh } from '@/lib/refresh';
import { useAuthStore } from '@/stores/auth';
import {
  CHECKOUT_WATCH_SLOW_PHASE_MS,
  checkoutWatchInterval,
  useCheckoutActivationStore,
} from '@/stores/checkoutActivation';
import { useToastStore } from '@/stores/toast';

const COACH_ROLES = new Set(['trainer', 'nutritionist']);

/** True once both coach roles are active, i.e. the purchased second role is present. */
function hasBothCoachRoles(roles: MyCoachRoles | undefined): boolean {
  if (!roles) return false;
  const active = new Set(roles.roles.map((summary) => summary.role.toLowerCase()));
  return [...COACH_ROLES].every((role) => active.has(role));
}

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
 * An add-on watch is done only when the server has added the paid role: the tokens are then refreshed so the JWT carries it.
 * Bounded polling is the agreed exception to the no-polling rule: no push event announces a webhook landing.
 */
export function CheckoutActivationWatcher() {
  const { i18n } = useTranslation();
  const queryClient = useQueryClient();
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
  // The server adds the paid role itself; the add-on watch only waits for it to show among the active roles.
  const rolesQuery = useQuery({
    queryKey: rolesKeys.mine,
    queryFn: getMyCoachRoles,
    enabled: watching && awaitsSecondRoleAddOn,
    refetchInterval,
    refetchOnWindowFocus: 'always',
  });

  const data = subscription.data;
  const rolesData = rolesQuery.data;
  const refetchSubscription = subscription.refetch;
  const refetchRoles = rolesQuery.refetch;
  const dataRef = useRef(data);
  const rolesRef = useRef(rolesData);
  const adopting = useRef(false);
  useEffect(() => {
    dataRef.current = data;
    rolesRef.current = rolesData;
  }, [data, rolesData]);

  // The JWT carries the roles, so a fresh pair is needed before the new role works. Uses the shared single-flight
  // refresh; resolves false when it failed, so the caller never reports a role the session cannot use.
  const adoptNewRole = useCallback(
    async (roles: MyCoachRoles): Promise<boolean> => {
      try {
        await executeRefresh();
      } catch {
        return false;
      }
      const { user, setUser } = useAuthStore.getState();
      if (user) {
        setUser({ ...user, roles: [...new Set([...user.roles, ...roles.roles.map((summary) => summary.role)])] });
      }
      void queryClient.invalidateQueries({ queryKey: profileKeys.me });
      void queryClient.invalidateQueries({ queryKey: rolesKeys.mine });
      return true;
    },
    [queryClient],
  );

  useEffect(() => {
    // Read the store rather than the closure so a repeated effect run cannot announce the plan twice.
    const state = useCheckoutActivationStore.getState();
    if (state.startedAt === null) return;
    if (state.awaitsSecondRoleAddOn) {
      // Done only when the new role is present, not when the add-on is merely paid. The role itself is added by the server.
      if (!isSecondRoleAddOnSettled(data) || !rolesData || !hasBothCoachRoles(rolesData)) return;
      if (adopting.current) return;
      adopting.current = true;
      const watchStartedAt = state.startedAt;
      void adoptNewRole(rolesData).then((adopted) => {
        adopting.current = false;
        const current = useCheckoutActivationStore.getState();
        if (!adopted || current.startedAt !== watchStartedAt || !current.awaitsSecondRoleAddOn) return;
        current.stop();
        useToastStore.getState().addToast(i18n.t('subscription.billing.addRole.roleAdded'), 'success');
      });
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
  }, [data, rolesData, i18n, adoptNewRole]);

  // An upgrade watch has an end: when the window closes without the plan landing, the payment was not confirmed and
  // the coach stays on the old plan. One last read first, so a late landing is not reported as a failure.
  useEffect(() => {
    if (!watching || startedAt === null || (targetPlanCode === null && !awaitsSecondRoleAddOn)) return;
    const timer = setTimeout(
      () => {
        void (async () => {
          const latest = (await refetchSubscription()).data ?? dataRef.current;
          const latestRoles = awaitsSecondRoleAddOn ? ((await refetchRoles()).data ?? rolesRef.current) : undefined;
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
            const paid = isSecondRoleAddOnSettled(latest);
            const adopted = paid && latestRoles && hasBothCoachRoles(latestRoles) ? await adoptNewRole(latestRoles) : false;
            if (adopted) {
              useToastStore.getState().addToast(i18n.t('subscription.billing.addRole.roleAdded'), 'success');
              return;
            }
            // A paid add-on without the role is not a failed payment: say so rather than a false success or failure.
            const key = paid ? 'subscription.billing.addRole.roleNotAppeared' : 'subscription.billing.addRole.notConfirmed';
            useToastStore.getState().addToast(i18n.t(key), 'error');
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
  }, [watching, startedAt, targetPlanCode, awaitsSecondRoleAddOn, refetchSubscription, refetchRoles, adoptNewRole, i18n]);

  return null;
}
