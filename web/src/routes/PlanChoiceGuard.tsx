import { useEffect, useState } from 'react';
import { Navigate, Outlet, useLocation, useNavigate } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { subscriptionKeys } from '@/api/subscription';
import { PLAN_CHOICE_PATH } from '@/components/subscription/planLinks';
import { useSubscription } from '@/hooks/useSubscriptionQueries';
import { PLAN_CHOICE_REQUIRED_EVENT } from '@/lib/subscriptionErrors';

/** Routes a coach can still use while the plan choice is pending (the backend keeps chat and account writes open). */
const ALLOWED_PATHS = [PLAN_CHOICE_PATH, '/inbox', '/settings', '/profile'];

function isAllowed(pathname: string): boolean {
  return ALLOWED_PATHS.some((path) => pathname === path || pathname.startsWith(`${path}/`));
}

/**
 * Keeps a coach on the plan decision screen while a plan choice or a keep-clients choice is pending. Redirects
 * on first load and on route changes only, never mid-screen. Also listens
 * for the API layer's PLAN_CHOICE_REQUIRED event, so a refused write from any screen refetches the subscription
 * and lands on the picker. An unknown or failed subscription lookup never locks the app.
 */
export default function PlanChoiceGuard() {
  const { pathname } = useLocation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const subscription = useSubscription();
  const pending = subscription.isCoach && subscription.query.isPending;
  const decisionRequired = subscription.planDecisionRequired;
  // The route the coach was last seen on while no decision was required. A decision that appears while they stay
  // on it (a live subscription refresh) does not pull them away; the next route change does.
  const [exemptPath, setExemptPath] = useState<string | null>(null);

  // Adjusted during render (own state only): remember the route while no decision is due, forget it once the
  // coach leaves that route with a decision pending.
  if (!pending) {
    if (!decisionRequired && exemptPath !== pathname) {
      setExemptPath(pathname);
    } else if (decisionRequired && exemptPath !== null && exemptPath !== pathname) {
      setExemptPath(null);
    }
  }

  useEffect(() => {
    const onRefused = () => {
      void queryClient.invalidateQueries({ queryKey: subscriptionKeys.mine });
      navigate(PLAN_CHOICE_PATH, { replace: true });
    };
    window.addEventListener(PLAN_CHOICE_REQUIRED_EVENT, onRefused);
    return () => window.removeEventListener(PLAN_CHOICE_REQUIRED_EVENT, onRefused);
  }, [navigate, queryClient]);

  if (pending) {
    return null;
  }

  if (decisionRequired && !isAllowed(pathname) && exemptPath !== pathname) {
    return <Navigate to={PLAN_CHOICE_PATH} replace />;
  }

  return <Outlet />;
}
