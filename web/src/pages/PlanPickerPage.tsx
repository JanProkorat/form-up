import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { ChevronLeft, CreditCard } from 'lucide-react';
import { BillingInterval, CheckoutFlow, PlanChangeTiming, PromotionDuration, type CoachPlanDto } from '@/api/generated';
import { billingErrorCode } from '@/components/subscription/billingErrors';
import { PLAN_BILLING_PATH } from '@/components/subscription/planLinks';
import PlanCard from '@/components/subscription/PlanCard';
import PlanConfirmBar from '@/components/subscription/PlanConfirmBar';
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
import UpgradePaymentNotice from '@/components/subscription/UpgradePaymentNotice';
import { usePlanSelection } from '@/components/subscription/usePlanSelection';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { useActiveRoles } from '@/hooks/useActiveRoles';
import { useCoachPlans, usePlanChangePreview, useSubscription } from '@/hooks/useSubscriptionQueries';

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
  const promotion = plansQuery.data?.promotion;
  const currentCode = data?.planCode ?? FREE_PLAN_CODE;
  const currentPlan = plans.find((plan) => plan.code === currentCode);
  const currentPrice = currentPlan ? planDisplayPrice(currentPlan, bothRoles) : 0;
  const endsAt =
    data?.cancelAtPeriodEnd && data.currentPeriodEndsAt
      ? new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' }).format(new Date(data.currentPeriodEndsAt))
      : null;
  // A change that can still be cancelled (a downgrade, or the end of a paid plan): the current plan becomes pickable
  // as "keep", and the plan it would switch to cannot be picked again.
  const scheduled = data?.scheduledPlan;
  const cancellable = scheduled?.cancellable === true;
  const scheduledCode = cancellable ? (scheduled?.planCode ?? null) : null;
  const scheduledOn = scheduled?.startsAt
    ? new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' }).format(new Date(scheduled.startsAt))
    : data?.currentPeriodEndsAt
      ? new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' }).format(new Date(data.currentPeriodEndsAt))
      : null;
  const switchPending = (data?.cancelAtPeriodEnd === true && selection.live) || scheduledCode === FREE_PLAN_CODE;
  const [selectedCode, setSelectedCode] = useState<string | null>(null);
  const [now] = useState(() => Date.now());
  const isSetByFormUp = data?.isSetByFormUp === true;
  // Dated only while the end date is still ahead, like the Settings plan card.
  const formUpPriceText = isSetByFormUp
    ? data?.freeUntil && new Date(data.freeUntil).getTime() > now
      ? t('subscription.billing.picker.providedByFormUpUntil', {
          date: new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' }).format(new Date(data.freeUntil)),
        })
      : t('subscription.billing.picker.providedByFormUp')
    : undefined;

  const actionFor = (plan: CoachPlanDto) => {
    const name = coachPlanName(plan, i18n.language);
    if (plan.code === currentCode) {
      if (cancellable) {
        return {
          label: t('subscription.billing.cancelChange.keep', { plan: name }),
          primary: true,
          disabled: false,
          onClick: () => selection.keepCurrent(name),
        };
      }
      return {
        label: t('subscription.billing.picker.yourPlan'),
        primary: false,
        disabled: true,
        onClick: () => undefined,
      };
    }
    if (plan.code === FREE_PLAN_CODE) {
      const freeDate = endsAt ?? scheduledOn;
      return {
        label: switchPending
          ? freeDate
            ? t('subscription.billing.picker.switchesToFree', { free: name, date: freeDate })
            : t('subscription.billing.picker.switchesToFreeNoDate', { free: name })
          : t('subscription.planChoice.card.switchFree'),
        primary: false,
        disabled: switchPending,
        onClick: () => selection.switchToFree(name),
      };
    }
    if (plan.code === scheduledCode) {
      return {
        label: scheduledOn
          ? t('subscription.billing.picker.switchesTo', { plan: name, date: scheduledOn })
          : t('subscription.billing.picker.switchesToNoDate', { plan: name }),
        primary: false,
        disabled: true,
        onClick: () => undefined,
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

  const cards = plans.map((plan) => {
    const action = actionFor(plan);
    const current = plan.code === currentCode;
    const free = plan.code === FREE_PLAN_CODE;
    // The current plan (unless its scheduled change can be cancelled) and a switch that is already scheduled cannot be picked.
    const selectable = current ? cancellable : !(free && switchPending) && plan.code !== scheduledCode;
    const currency = plan.currency ?? 'CZK';
    const addOn = secondRoleAddOnOf(plan);
    const fullPrice = planDisplayPrice(plan, bothRoles);
    const discounted = planDiscountedPrice(plan, bothRoles);
    const offer = promotion && discounted !== null && discounted < fullPrice ? promotion : null;
    const breakdown =
      bothRoles && addOn !== null
        ? t('subscription.billing.picker.breakdown', {
            plan: coachPlanName(plan, i18n.language),
            base: formatMoney(plan.priceMinorUnits ?? 0, currency, i18n.language),
            addOn: formatMoney(addOn, currency, i18n.language),
          })
        : null;
    const isZero = (plan.priceMinorUnits ?? 0) === 0;
    const note = isZero
      ? t('subscription.billing.picker.noCard')
      : [breakdown, offer ? promotionShortText(t, offer, i18n.language) : null].filter(Boolean).join(' · ') ||
        undefined;
    // The bar spells out the full price after a limited promotion, which the card leaves out.
    const barOffer = offer
      ? offer.duration === PromotionDuration.Forever
        ? promotionShortText(t, offer, i18n.language)
        : t('subscription.promotion.card.then', {
            short: promotionShortText(t, offer, i18n.language),
            price: formatMoney(fullPrice, currency, i18n.language),
          })
      : null;
    const barNote = isZero
      ? t('subscription.billing.picker.noCard')
      : [breakdown, barOffer].filter(Boolean).join(' · ') || undefined;
    const interval = plan.billingInterval === BillingInterval.Annual ? 'year' : 'month';
    const barTitle = t('subscription.billing.select.planLine', {
      plan: coachPlanName(plan, i18n.language),
      price: formatMoney(offer ? (discounted ?? fullPrice) : fullPrice, currency, i18n.language),
      per: t(`subscription.planChoice.card.per.${interval}`),
    });
    return {
      plan,
      action,
      current,
      selectable,
      fullPrice,
      discounted,
      offer,
      note,
      barNote,
      barTitle,
      higher: !free && fullPrice > currentPrice,
    };
  });
  // A choice that stopped being selectable (it became the current plan, or its switch got scheduled) counts as none.
  const selected = cards.find((card) => card.selectable && card.plan.code === selectedCode);

  // A live paid subscription asks the server what a paid target would charge and when it starts.
  const selectedIsFree = selected?.plan.code === FREE_PLAN_CODE;
  const selectedIsKeep = selected?.current === true;
  const previewQuery = usePlanChangePreview(
    selected && !selectedIsFree && !selectedIsKeep ? (selected.plan.code ?? null) : null,
    selection.live,
  );
  const dateFormat = new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' });
  const formatDate = (iso: string | undefined) => (iso ? dateFormat.format(new Date(iso)) : null);
  const periodEndDate = formatDate(data?.currentPeriodEndsAt);
  const currentName = subscription.planName;
  const freePlanDto = plans.find((plan) => plan.code === FREE_PLAN_CODE);
  const freeName = freePlanDto ? coachPlanName(freePlanDto, i18n.language) : FREE_PLAN_CODE;

  const scheduledTargetPlan = plans.find((plan) => plan.code === scheduled?.planCode);
  const scheduledTargetName = scheduledTargetPlan
    ? coachPlanName(scheduledTargetPlan, i18n.language)
    : (scheduled?.planCode ?? freeName);

  const barHint = ((): { hint: string; extraHint?: string } => {
    const checkoutHint = { hint: t('subscription.billing.select.hintStripePages') };
    const periodHint = { hint: t('subscription.billing.select.hintPeriodEnd') };
    if (!selected) return checkoutHint;
    if (selectedIsKeep) {
      return {
        hint: scheduledOn
          ? t('subscription.billing.cancelChange.hint', { target: scheduledTargetName, date: scheduledOn, plan: currentName })
          : t('subscription.billing.cancelChange.hintNoDate', { target: scheduledTargetName, plan: currentName }),
      };
    }
    const planLabel = coachPlanName(selected.plan, i18n.language);
    const startsOn = (date: string | null) =>
      date
        ? { hint: t('subscription.billing.select.hintStartsOn', { plan: planLabel, date, current: currentName }) }
        : periodHint;
    // A plan set by Form Up has no subscription to change: paying starts a new one and ends the free period.
    if (isSetByFormUp && !selectedIsFree) {
      return { ...checkoutHint, extraHint: t('subscription.billing.select.hintEndsFormUpPlan', { plan: planLabel }) };
    }
    if (!selection.live) return selected.higher ? checkoutHint : periodHint;
    if (selectedIsFree) return startsOn(periodEndDate);

    if (previewQuery.isPending) return { hint: t('subscription.billing.select.hintPreviewLoading') };
    if (previewQuery.isError) {
      return billingErrorCode(previewQuery.error) === 'BILLING_NO_SUBSCRIPTION'
        ? checkoutHint
        : { hint: t('subscription.billing.select.hintPreviewError') };
    }
    const preview = previewQuery.data;
    if (preview.timing === PlanChangeTiming.AtNextRenewal) {
      return startsOn(formatDate(preview.nextChargeAt) ?? periodEndDate);
    }
    const currency = preview.currency ?? 'CZK';
    const nextDate = formatDate(preview.nextChargeAt);
    const annual = preview.billingInterval === BillingInterval.Annual;
    const per = t(`subscription.planChoice.card.per.${annual ? 'year' : 'month'}`);
    const span = t(`subscription.billing.select.${annual ? 'hintSpanYear' : 'hintSpanMonth'}`);
    const today = formatMoney(preview.todayAmountMinorUnits ?? 0, currency, i18n.language);
    const next = formatMoney(preview.nextAmountMinorUnits ?? 0, currency, i18n.language);
    return {
      hint: nextDate
        ? t('subscription.billing.select.hintUpgradeToday', { today, next, per, span, date: nextDate })
        : t('subscription.billing.select.hintUpgradeTodayNoDate', { today, next, per, span }),
      extraHint: preview.cancelsScheduledSwitchToFree
        ? t('subscription.billing.select.hintCancelsFree', { free: freeName })
        : undefined,
    };
  })();

  const rolesLine = bothRoles
    ? t('subscription.billing.picker.rolesBoth', { first: t('roles.trainer'), second: t('roles.nutritionist') })
    : t('subscription.billing.picker.rolesOne', { role: t(isNutritionist ? 'roles.nutritionist' : 'roles.trainer') });

  return (
    <div className="-mb-6 flex min-h-[calc(100%+--spacing(6))] flex-col gap-5">
      <div className="flex items-center gap-3.5">
        <Button asChild variant="outline" size="icon-lg" className="size-11 shrink-0">
          <Link to={PLAN_BILLING_PATH} aria-label={t('subscription.billing.picker.back')}>
            <ChevronLeft aria-hidden="true" />
          </Link>
        </Button>
        <div className="flex flex-col gap-1.5">
          <h1 className="font-display text-display font-semibold text-ink">{t('subscription.billing.picker.title')}</h1>
          <p className="text-copy text-muted-foreground">{t('subscription.billing.picker.subtitle')}</p>
        </div>
      </div>

      {promotion && <PromotionBanner promotion={promotion} plans={plans} variant="picker" />}

      <div className="flex flex-wrap items-center gap-x-4 gap-y-1">
        <p className="text-copy text-muted-foreground">{rolesLine}</p>
        <p className="ml-auto text-body text-muted-foreground">{t('subscription.billing.codeAtCheckout')}</p>
      </div>

      {selection.awaitingPayment && (
        <UpgradePaymentNotice
          plan={selection.awaitingPayment.planName}
          current={currentName}
          paymentUrl={selection.awaitingPayment.paymentUrl}
          onOpen={selection.startPaymentWatch}
          onDismiss={selection.dismissAwaitingPayment}
        />
      )}

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
          {cards.map(({ plan, action, current, selectable, fullPrice, discounted, offer, note }) => (
            <PlanCard
              key={plan.code}
              plan={plan}
              current={current}
              badge={current ? t('subscription.billing.picker.yourPlan') : undefined}
              actionLabel={action.label}
              primary={action.primary}
              actionDisabled={action.disabled}
              busy={selection.busy}
              priceMinorUnits={offer ? (discounted ?? fullPrice) : fullPrice}
              originalPriceMinorUnits={offer ? fullPrice : undefined}
              priceText={current ? formUpPriceText : undefined}
              priceNote={current && formUpPriceText ? undefined : note}
              onAction={action.onClick}
              selection={
                selectable
                  ? {
                      selected: plan.code === selected?.plan.code,
                      onSelect: () => setSelectedCode(plan.code ?? null),
                      selectLabel: t('subscription.billing.select.select', { plan: coachPlanName(plan, i18n.language) }),
                    }
                  : undefined
              }
            />
          ))}
        </div>
      )}

      <TaxNote taxMode={plansQuery.data?.taxMode} />

      <p className="flex items-center gap-2.5 text-body text-muted-foreground">
        <CreditCard className="size-4 shrink-0" aria-hidden="true" />
        {t('subscription.billing.picker.footer')}
      </p>

      {selected && (
        <PlanConfirmBar
          bleed
          title={selected.barTitle}
          note={selected.barNote}
          hint={barHint.hint}
          extraHint={barHint.extraHint}
          confirmLabel={selected.action.label}
          busy={selection.busy}
          onConfirm={selected.action.onClick}
        />
      )}
    </div>
  );
}
