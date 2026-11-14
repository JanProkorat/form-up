import { useEffect } from 'react';
import { Navigate, useLocation, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useQueryClient } from '@tanstack/react-query';
import { Loader2 } from 'lucide-react';
import { profileKeys } from '@/api/profile';
import { subscriptionKeys } from '@/api/subscription';
import {
  BILLING_CANCEL_PATH,
  BILLING_SUCCESS_PATH,
  PLAN_BILLING_PATH,
  PLAN_PICKER_PATH,
  REGISTER_PLAN_PATH,
} from '@/components/subscription/planLinks';
import { useCheckoutReturn } from '@/hooks/useSubscriptionQueries';
import { useToastStore } from '@/stores/toast';

/** The query value Stripe's return URL carries; the backend appends it from the checkout request's flow. */
function isSignUpFlow(value: string | null): boolean {
  return value?.toLowerCase() === 'signup';
}

/** Waits for the webhook after a successful checkout, then continues: profile setup or Plan and billing. */
function CheckoutSuccess({ signUp }: { signUp: boolean }) {
  const { t } = useTranslation();
  const { settled, timedOut, isCoach } = useCheckoutReturn();
  const destination = signUp ? '/profile' : PLAN_BILLING_PATH;
  const done = settled || timedOut || !isCoach;

  // The note outlives this page: it is shown as a toast on the screen the coach lands on.
  useEffect(() => {
    if (timedOut && !settled) {
      useToastStore.getState().addToast(t('subscription.billing.returning.pending'), 'success');
    }
  }, [timedOut, settled, t]);

  if (done) {
    return <Navigate to={destination} replace />;
  }

  return (
    <div className="flex min-h-screen flex-col items-center justify-center gap-3 bg-background px-6 text-center">
      <Loader2 className="size-7 animate-spin text-muted-foreground motion-reduce:animate-none" aria-hidden="true" />
      <h1 className="font-display text-card-title font-semibold text-ink">{t('subscription.billing.returning.title')}</h1>
      <p role="status" className="text-copy text-muted-foreground">
        {t('subscription.billing.returning.body')}
      </p>
    </div>
  );
}

/** The Customer Portal sent the coach back: re-read the subscription, then show Plan and billing. */
function PortalReturn() {
  const queryClient = useQueryClient();
  useEffect(() => {
    void queryClient.invalidateQueries({ queryKey: subscriptionKeys.mine });
    void queryClient.invalidateQueries({ queryKey: profileKeys.me });
  }, [queryClient]);
  return <Navigate to={PLAN_BILLING_PATH} replace />;
}

/**
 * The page Stripe sends the coach back to: `/billing/success` and `/billing/cancel` after Checkout, `/billing`
 * after the Customer Portal. `?flow=signup` marks a checkout started in the sign-up plan step.
 */
export default function BillingReturnPage() {
  const { pathname } = useLocation();
  const [searchParams] = useSearchParams();
  const signUp = isSignUpFlow(searchParams.get('flow'));

  if (pathname === BILLING_SUCCESS_PATH) {
    return <CheckoutSuccess signUp={signUp} />;
  }
  if (pathname === BILLING_CANCEL_PATH) {
    return <Navigate to={signUp ? REGISTER_PLAN_PATH : PLAN_PICKER_PATH} replace />;
  }
  return <PortalReturn />;
}
