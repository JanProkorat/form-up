import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { CheckoutFlow, PlanChangeTiming, type ChangePlanResponse, type CoachPlanDto } from '@/api/generated';
import { profileKeys } from '@/api/profile';
import { changePlan, createCheckoutSession, subscriptionKeys, switchToFree } from '@/api/subscription';
import { billingErrorCode, showBillingError } from '@/components/subscription/billingErrors';
import { coachPlanName } from '@/components/subscription/planNames';
import { hasLivePaidSubscription } from '@/components/subscription/planPricing';
import { useSubscription } from '@/hooks/useSubscriptionQueries';
import { useToastStore } from '@/stores/toast';

type PlanOutcome = { kind: 'checkout'; url: string } | { kind: 'changed'; response: ChangePlanResponse };

interface PaidPlanRequest {
  plan: CoachPlanDto;
  includeSecondRoleAddOn: boolean;
}

/**
 * Taking a paid plan: a coach with a live subscription changes it in place, anyone else goes to Stripe
 * Checkout. Each path falls back to the other once when the server says it is the wrong one.
 */
async function selectPaidPlan(
  { plan, includeSecondRoleAddOn }: PaidPlanRequest,
  flow: CheckoutFlow,
  live: boolean,
): Promise<PlanOutcome> {
  const planCode = plan.code ?? '';
  const checkout = async (): Promise<PlanOutcome> => ({
    kind: 'checkout',
    url: await createCheckoutSession({ planCode, includeSecondRoleAddOn, flow }),
  });
  const change = async (): Promise<PlanOutcome> => ({ kind: 'changed', response: await changePlan(planCode) });

  if (live) {
    try {
      return await change();
    } catch (error) {
      if (billingErrorCode(error) === 'BILLING_NO_SUBSCRIPTION') return checkout();
      throw error;
    }
  }
  try {
    return await checkout();
  } catch (error) {
    if (billingErrorCode(error) === 'BILLING_ALREADY_SUBSCRIBED') return change();
    throw error;
  }
}

/**
 * The plan actions behind the plan picker and the plan decision screen: take a paid plan (checkout or
 * in-place change) and switch a paying coach to Free at the end of the period.
 */
export function usePlanSelection(flow: CheckoutFlow) {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const subscription = useSubscription();
  const live = hasLivePaidSubscription(subscription.data);

  const dateFormat = new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' });
  const toast = (message: string) => useToastStore.getState().addToast(message, 'success');
  const refresh = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: subscriptionKeys.mine }),
      queryClient.invalidateQueries({ queryKey: subscriptionKeys.plans }),
      queryClient.invalidateQueries({ queryKey: profileKeys.me }),
    ]);

  const paidPlan = useMutation({
    mutationFn: (request: PaidPlanRequest) => selectPaidPlan(request, flow, live),
    onSuccess: (outcome, { plan }) => {
      if (outcome.kind === 'checkout') {
        window.location.assign(outcome.url);
        return;
      }
      const name = coachPlanName(plan, i18n.language);
      const { timing, effectiveAt } = outcome.response;
      if (timing === PlanChangeTiming.AtNextRenewal) {
        toast(
          effectiveAt
            ? t('subscription.billing.changed.atRenewal', { plan: name, date: dateFormat.format(new Date(effectiveAt)) })
            : t('subscription.billing.changed.atRenewalNoDate', { plan: name }),
        );
      } else {
        toast(t('subscription.billing.changed.now', { plan: name }));
      }
      void refresh();
    },
    onError: (error) => {
      showBillingError(error, 'subscription.billing.errors.changePlan');
      // The subscription may have changed under us (for example a cancellation already pending): re-read it.
      void queryClient.invalidateQueries({ queryKey: subscriptionKeys.mine });
    },
  });

  const freePlan = useMutation({
    mutationFn: switchToFree,
    onSuccess: ({ endsAt }) => {
      toast(
        endsAt
          ? t('subscription.billing.switchFree.scheduled', { date: dateFormat.format(new Date(endsAt)) })
          : t('subscription.billing.switchFree.scheduledNoDate'),
      );
      void refresh();
    },
    onError: (error) => {
      showBillingError(error, 'subscription.billing.errors.switchFree');
      void queryClient.invalidateQueries({ queryKey: subscriptionKeys.mine });
    },
  });

  return {
    live,
    /** Takes a paid plan: in place for a live subscription, through Stripe Checkout otherwise. */
    choosePaid: (plan: CoachPlanDto, includeSecondRoleAddOn: boolean) => paidPlan.mutate({ plan, includeSecondRoleAddOn }),
    /** Ends the paid plan at the end of the period. */
    switchToFree: () => freePlan.mutate(),
    /** Code of the paid plan being taken right now. */
    pendingPlanCode: paidPlan.isPending ? (paidPlan.variables?.plan.code ?? null) : null,
    /** True while a Stripe redirect is under way: the page is about to be replaced, so keep the buttons off. */
    redirecting: paidPlan.isSuccess && paidPlan.data.kind === 'checkout',
    switchingToFree: freePlan.isPending,
    busy: paidPlan.isPending || freePlan.isPending || (paidPlan.isSuccess && paidPlan.data.kind === 'checkout'),
  };
}
