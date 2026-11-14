import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { ChevronLeft, CreditCard } from 'lucide-react';
import { CheckoutFlow, type CoachPlanDto } from '@/api/generated';
import PlanCard from '@/components/subscription/PlanCard';
import TaxNote from '@/components/subscription/TaxNote';
import { formatMoney } from '@/components/subscription/money';
import { coachPlanName } from '@/components/subscription/planNames';
import { FREE_PLAN_CODE, planDisplayPrice, secondRoleAddOnOf } from '@/components/subscription/planPricing';
import { usePlanSelection } from '@/components/subscription/usePlanSelection';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { useActiveRoles } from '@/hooks/useActiveRoles';
import { useCoachPlans, useSubscription } from '@/hooks/useSubscriptionQueries';

/**
 * "Choose your plan" for a coach who is already in the portal: four plan cards with the price for the
 * coach's roles, the current plan outlined, and the actions that change it (Stripe Checkout, an in-place
 * change, or a switch to Free at the end of the period).
 */
export default function PlanPickerPage() {
  const { t, i18n } = useTranslation();
  const subscription = useSubscription();
  const plansQuery = useCoachPlans(subscription.isCoach);
  const selection = usePlanSelection(CheckoutFlow.Settings);
  const { hasRole } = useActiveRoles();
  const isTrainer = hasRole('Trainer');
  const isNutritionist = hasRole('Nutritionist');
  const bothRoles = isTrainer && isNutritionist;

  const data = subscription.data;
  const plans = plansQuery.data?.plans ?? [];
  const currentCode = data?.planCode ?? FREE_PLAN_CODE;
  const currentPlan = plans.find((plan) => plan.code === currentCode);
  const currentPrice = currentPlan ? planDisplayPrice(currentPlan, bothRoles) : 0;
  const endsAt =
    data?.cancelAtPeriodEnd && data.currentPeriodEndsAt
      ? new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' }).format(new Date(data.currentPeriodEndsAt))
      : null;
  const switchPending = data?.cancelAtPeriodEnd === true && selection.live;

  const actionFor = (plan: CoachPlanDto) => {
    const name = coachPlanName(plan, i18n.language);
    if (plan.code === currentCode) {
      return {
        label: t('subscription.billing.picker.yourPlan'),
        primary: false,
        disabled: true,
        onClick: () => undefined,
      };
    }
    if (plan.code === FREE_PLAN_CODE) {
      return {
        label: switchPending
          ? endsAt
            ? t('subscription.billing.picker.switchesToFree', { date: endsAt })
            : t('subscription.billing.picker.switchesToFreeNoDate')
          : t('subscription.planChoice.card.switchFree'),
        primary: false,
        disabled: switchPending,
        onClick: selection.switchToFree,
      };
    }
    const higher = planDisplayPrice(plan, bothRoles) > currentPrice;
    return {
      label: t(higher ? 'subscription.planChoice.card.upgrade' : 'subscription.billing.picker.switchTo', { plan: name }),
      primary: higher,
      disabled: false,
      onClick: () => selection.choosePaid(plan, bothRoles && secondRoleAddOnOf(plan) !== null),
    };
  };

  const rolesLine = bothRoles
    ? t('subscription.billing.picker.rolesBoth', { first: t('roles.trainer'), second: t('roles.nutritionist') })
    : t('subscription.billing.picker.rolesOne', { role: t(isNutritionist ? 'roles.nutritionist' : 'roles.trainer') });

  return (
    <div className="flex flex-col gap-5">
      <div className="flex flex-wrap items-end gap-3">
        <div className="flex flex-col gap-1.5">
          <h1 className="font-display text-display font-semibold text-ink">{t('subscription.billing.picker.title')}</h1>
          <p className="text-copy text-muted-foreground">{t('subscription.billing.picker.subtitle')}</p>
        </div>
        <Button asChild variant="outline" size="lg" className="ml-auto gap-1.75 px-3.5 font-semibold">
          <Link to="/settings">
            <ChevronLeft aria-hidden="true" />
            {t('subscription.billing.picker.back')}
          </Link>
        </Button>
      </div>

      <div className="flex flex-wrap items-center gap-x-4 gap-y-1">
        <p className="text-copy text-muted-foreground">{rolesLine}</p>
        <p className="ml-auto text-body text-muted-foreground">{t('subscription.billing.codeAtCheckout')}</p>
      </div>

      {plansQuery.isError ? (
        <div role="alert" className="flex flex-col items-start gap-3">
          <p className="text-copy text-error">{t('subscription.planChoice.loadError')}</p>
          <Button
            type="button"
            variant="outline"
            size="lg"
            className="px-3.5 font-semibold"
            onClick={() => void plansQuery.refetch()}
          >
            {t('subscription.lapse.retry')}
          </Button>
        </div>
      ) : plansQuery.isPending || subscription.query.isPending ? (
        <Skeleton className="h-96 w-full rounded-2xl" />
      ) : (
        <div className="grid items-stretch gap-4 sm:grid-cols-2 xl:grid-cols-4">
          {plans.map((plan) => {
            const action = actionFor(plan);
            const current = plan.code === currentCode;
            const currency = plan.currency ?? 'CZK';
            const addOn = secondRoleAddOnOf(plan);
            const note =
              (plan.priceMinorUnits ?? 0) === 0
                ? t('subscription.billing.picker.noCard')
                : bothRoles && addOn !== null
                  ? t('subscription.billing.picker.breakdown', {
                      plan: coachPlanName(plan, i18n.language),
                      base: formatMoney(plan.priceMinorUnits ?? 0, currency, i18n.language),
                      addOn: formatMoney(addOn, currency, i18n.language),
                    })
                  : undefined;
            return (
              <PlanCard
                key={plan.code}
                plan={plan}
                current={current}
                badge={current ? t('subscription.billing.picker.yourPlan') : undefined}
                actionLabel={action.label}
                primary={action.primary}
                actionDisabled={action.disabled}
                busy={selection.busy}
                priceMinorUnits={planDisplayPrice(plan, bothRoles)}
                priceNote={note}
                onAction={action.onClick}
              />
            );
          })}
        </div>
      )}

      <TaxNote taxMode={plansQuery.data?.taxMode} />

      <p className="flex items-center gap-2.5 text-body text-muted-foreground">
        <CreditCard className="size-4 shrink-0" aria-hidden="true" />
        {t('subscription.billing.picker.footer')}
      </p>
    </div>
  );
}
