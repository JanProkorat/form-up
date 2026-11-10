import { useEffect } from 'react';
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
 * Keeps a coach on the plan picker while their admin-given plan has ended and no plan is chosen. Also listens
 * for the API layer's PLAN_CHOICE_REQUIRED event, so a refused write from any screen refetches the subscription
 * and lands on the picker. An unknown or failed subscription lookup never locks the app.
 */
export default function PlanChoiceGuard() {
  const { pathname } = useLocation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const subscription = useSubscription();

  useEffect(() => {
    const onRefused = () => {
      void queryClient.invalidateQueries({ queryKey: subscriptionKeys.mine });
      navigate(PLAN_CHOICE_PATH, { replace: true });
    };
    window.addEventListener(PLAN_CHOICE_REQUIRED_EVENT, onRefused);
    return () => window.removeEventListener(PLAN_CHOICE_REQUIRED_EVENT, onRefused);
  }, [navigate, queryClient]);

  if (subscription.isCoach && subscription.query.isPending) {
    return null;
  }

  if (subscription.planChoiceRequired && !isAllowed(pathname)) {
    return <Navigate to={PLAN_CHOICE_PATH} replace />;
  }

  return <Outlet />;
}
