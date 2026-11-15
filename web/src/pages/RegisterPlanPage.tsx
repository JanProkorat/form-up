import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { CreditCard } from 'lucide-react';
import { CheckoutFlow, PromotionDuration } from '@/api/generated';
import RegisterShell from '@/components/register/RegisterShell';
import RegisterStepper from '@/components/register/RegisterStepper';
import PlanCard from '@/components/subscription/PlanCard';
import TaxNote from '@/components/subscription/TaxNote';
import { formatMoney } from '@/components/subscription/money';
import { coachPlanName } from '@/components/subscription/planNames';
import {
  FREE_PLAN_CODE,
  planDiscountedPrice,
  planDisplayPrice,
  secondRoleAddOnOf,
} from '@/components/subscription/planPricing';
import PromotionBanner from '@/components/subscription/PromotionBanner';
import { promotionShortText } from '@/components/subscription/promotionText';
import { usePlanSelection } from '@/components/subscription/usePlanSelection';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { useActiveRoles } from '@/hooks/useActiveRoles';
import { useCoachPlans, useSubscription } from '@/hooks/useSubscriptionQueries';
import { useAuthStore } from '@/stores/auth';

/**
 * Sign-up plan step, the first part of step 3 "Your profile": Free is the default and needs no card, a paid
 * plan goes through Stripe Checkout and comes back to profile setup. A coach who signed up with one role sees
 * the one-role prices; one who signed up with both sees the combined prices.
 */
export default function RegisterPlanPage() {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const email = useAuthStore((s) => s.user?.email ?? '');
  const subscription = useSubscription();
  const plansQuery = useCoachPlans(subscription.isCoach);
  const selection = usePlanSelection(CheckoutFlow.SignUp);
  const { hasRole, isLoading: rolesLoading } = useActiveRoles();
  // A failed roles lookup leaves both roles absent, so the one-role prices show; checkout bills from the roles.
  const bothRoles = hasRole('Trainer') && hasRole('Nutritionist');
  const plans = plansQuery.data?.plans ?? [];
  const promotion = plansQuery.data?.promotion;

  const startFree = () => navigate('/profile', { replace: true });

  return (
    <RegisterShell
      showSignIn={false}
      trailing={email ? t('subscription.billing.register.signedInAs', { email }) : undefined}
    >
      <main className="flex flex-1 flex-col gap-5 px-4 pt-2 pb-16 sm:px-10 lg:px-30">
        <div className="mx-auto flex w-full max-w-140 flex-col gap-5.5 text-center">
          <div className="text-left">
            <RegisterStepper current={3} finalStep="profile" />
          </div>
          <div className="flex flex-col gap-1.5">
            <h1 className="font-display text-display font-semibold tracking-heading text-ink">
              {t('subscription.billing.register.title')}
            </h1>
            <p className="text-subhead text-ink-2">{t('subscription.billing.register.subtitle')}</p>
          </div>
        </div>

        {promotion && (
          <div className="mx-auto w-full max-w-140">
            <PromotionBanner promotion={promotion} plans={plans} variant="register" />
          </div>
        )}

        <p className="text-center text-body text-muted-foreground">{t('subscription.billing.codeAtCheckout')}</p>

        {bothRoles && (
          <p className="text-center text-copy text-muted-foreground">
            {t('subscription.billing.register.rolesBoth', {
              first: t('roles.trainer'),
              second: t('roles.nutritionist'),
            })}
          </p>
        )}

        {plansQuery.isError ? (
          <div role="alert" className="flex flex-col items-center gap-3">
            <p className="text-copy text-error">{t('subscription.planChoice.loadError')}</p>
            <div className="flex gap-2.5">
              <Button
                type="button"
                variant="outline"
                size="lg"
                className="px-3.5 font-semibold"
                onClick={() => void plansQuery.refetch()}
              >
                {t('subscription.lapse.retry')}
              </Button>
              <Button type="button" size="lg" className="px-3.5 font-semibold" onClick={startFree}>
                {t('subscription.billing.register.startFree')}
              </Button>
            </div>
          </div>
        ) : plansQuery.isPending || rolesLoading ? (
          <Skeleton className="h-96 w-full rounded-2xl" />
        ) : (
          <div className="grid items-stretch gap-4 sm:grid-cols-2 xl:grid-cols-4">
            {plans.map((plan) => {
              const free = plan.code === FREE_PLAN_CODE;
              const name = coachPlanName(plan, i18n.language);
              const addOn = secondRoleAddOnOf(plan);
              const currency = plan.currency ?? 'CZK';
              const secondRoleText = free
                ? bothRoles
                  ? t('subscription.planChoice.card.bothRoles')
                  : t('subscription.billing.register.secondRoleFree')
                : addOn !== null
                  ? t(bothRoles ? 'subscription.billing.register.secondRoleIncluded' : 'subscription.billing.register.secondRoleLater', {
                      amount: formatMoney(addOn, currency, i18n.language),
                    })
                  : bothRoles && plan.bothRolesIncluded
                    ? t('subscription.planChoice.card.bothRoles')
                    : undefined;
              const fullPrice = planDisplayPrice(plan, bothRoles);
              const discounted = planDiscountedPrice(plan, bothRoles);
              const offer = promotion && discounted !== null && discounted < fullPrice ? promotion : null;
              const offerNote = offer
                ? offer.duration === PromotionDuration.Forever
                  ? promotionShortText(t, offer, i18n.language)
                  : t('subscription.promotion.card.then', {
                      short: promotionShortText(t, offer, i18n.language),
                      price: formatMoney(fullPrice, currency, i18n.language),
                    })
                : undefined;
              const breakdown =
                bothRoles && addOn !== null
                  ? t('subscription.billing.picker.breakdown', {
                      plan: name,
                      base: formatMoney(plan.priceMinorUnits ?? 0, currency, i18n.language),
                      addOn: formatMoney(addOn, currency, i18n.language),
                    })
                  : null;
              const note = [breakdown, offerNote].filter(Boolean).join(' · ') || undefined;
              return (
                <PlanCard
                  key={plan.code}
                  plan={plan}
                  emphasized={free}
                  badge={free ? t('subscription.billing.register.noCardBadge') : undefined}
                  actionLabel={
                    free
                      ? t('subscription.billing.register.startFree')
                      : t('subscription.billing.register.choose', { plan: name })
                  }
                  primary={free}
                  actionDisabled={false}
                  busy={selection.busy}
                  priceMinorUnits={offer ? (discounted ?? fullPrice) : fullPrice}
                  originalPriceMinorUnits={offer ? fullPrice : undefined}
                  priceNote={free ? t('subscription.billing.picker.noCard') : note}
                  secondRoleText={secondRoleText}
                  onAction={free ? startFree : () => selection.choosePaid(plan, bothRoles && addOn !== null)}
                />
              );
            })}
          </div>
        )}

        <TaxNote taxMode={plansQuery.data?.taxMode} className="text-center text-body text-muted-foreground" />

        <p className="flex items-center justify-center gap-2.5 text-center text-body text-muted-foreground">
          <CreditCard className="size-4 shrink-0" aria-hidden="true" />
          {t('subscription.billing.register.footer')}
        </p>
      </main>
    </RegisterShell>
  );
}
