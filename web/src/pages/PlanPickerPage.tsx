import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { ChevronLeft, CreditCard } from 'lucide-react';
import { BillingInterval, CheckoutFlow, PlanChangeTiming, PromotionDuration } from '@/api/generated';
import BillingIntervalSwitch from '@/components/subscription/BillingIntervalSwitch';
import { billingErrorCode, billingErrorMessage } from '@/components/subscription/billingErrors';
import { PLAN_BILLING_PATH } from '@/components/subscription/planLinks';
import PlanCard from '@/components/subscription/PlanCard';
import PlanConfirmBar from '@/components/subscription/PlanConfirmBar';
import TaxNote from '@/components/subscription/TaxNote';
import { formatApproxMoney, formatMoney } from '@/components/subscription/money';
import { coachPlanName } from '@/components/subscription/planNames';
import {
  FREE_PLAN_CODE,
  anyPlanSoldAnnually,
  effectiveInterval,
  maxAnnualSavingPercent,
  planDiscountedPriceFor,
  planDisplayPrice,
  planPriceFor,
  secondRoleAddOnFor,
  secondRoleAddOnOf,
} from '@/components/subscription/planPricing';
import PromotionBanner from '@/components/subscription/PromotionBanner';
import PromotionCodeField from '@/components/subscription/PromotionCodeField';
import { promotionAnnualNote, promotionShortText } from '@/components/subscription/promotionText';
import UpgradePaymentNotice from '@/components/subscription/UpgradePaymentNotice';
import { usePlanSelection } from '@/components/subscription/usePlanSelection';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { useActiveRoles } from '@/hooks/useActiveRoles';
import { useCoachPlans, usePlanChangePreview, useSubscription } from '@/hooks/useSubscriptionQueries';

/**
 * "Choose your plan" for a coach who is already in the portal: four plan cards with the price for the
 * coach's roles on the chosen billing period, the current plan outlined, and the actions that change it
 * (Stripe Checkout, an in-place change, or a switch to Free at the end of the period).
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
  // Only a plan ranking: the card prices below follow the chosen billing period.
  const currentRank = currentPlan ? planDisplayPrice(currentPlan, bothRoles) : 0;
  // The interval the coach pays now; a plan without a live Stripe subscription (Free, set by Form Up) has none to compare.
  const currentInterval = selection.live ? (data?.billingInterval ?? BillingInterval.Monthly) : BillingInterval.Monthly;
  const endsAt =
    data?.cancelAtPeriodEnd && data.currentPeriodEndsAt
      ? new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' }).format(new Date(data.currentPeriodEndsAt))
      : null;
  // A change that can still be cancelled (a downgrade, or the end of a paid plan): the current plan becomes pickable
  // as "keep", and the plan it would switch to cannot be picked again.
  const scheduled = data?.scheduledPlan;
  const cancellable = scheduled?.cancellable === true;
  const scheduledCode = cancellable ? (scheduled?.planCode ?? null) : null;
  const scheduledInterval = scheduled?.billingInterval ?? BillingInterval.Monthly;
  const scheduledOn = scheduled?.startsAt
    ? new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' }).format(new Date(scheduled.startsAt))
    : data?.currentPeriodEndsAt
      ? new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' }).format(new Date(data.currentPeriodEndsAt))
      : null;
  const switchPending = (data?.cancelAtPeriodEnd === true && selection.live) || scheduledCode === FREE_PLAN_CODE;
  const [selectedCode, setSelectedCode] = useState<string | null>(null);
  // The switch only exists while some plan is sold annually, or the coach already pays annually.
  const [chosenInterval, setChosenInterval] = useState<BillingInterval | null>(null);
  const showSwitch = anyPlanSoldAnnually(plans) || currentInterval === BillingInterval.Annual;
  const pageInterval = showSwitch ? (chosenInterval ?? currentInterval) : BillingInterval.Monthly;
  // The typed code lives in the browser only: not in the URL, a query key or storage. A checkout request carries it.
  const [appliedCode, setAppliedCode] = useState<string | null>(null);
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

  const cards = plans.map((plan) => {
    const name = coachPlanName(plan, i18n.language);
    const current = plan.code === currentCode;
    const free = plan.code === FREE_PLAN_CODE;
    const interval = effectiveInterval(plan, pageInterval);
    const annual = interval === BillingInterval.Annual;
    // The same plan on the same billing period, which cannot be picked again. Free and a plan without a live Stripe
    // subscription have no period to differ on.
    const sameAsCurrent = current && (free || !selection.live || interval === currentInterval);
    const keep = sameAsCurrent && cancellable;
    const scheduledHere = plan.code === scheduledCode && (free || interval === scheduledInterval);
    const currency = plan.currency ?? 'CZK';
    const addOn = secondRoleAddOnFor(plan, interval);
    const fullPrice = planPriceFor(plan, interval, bothRoles);
    const discounted = planDiscountedPriceFor(plan, interval, bothRoles);
    const offer = promotion && discounted !== null && discounted < fullPrice ? promotion : null;
    const breakdown =
      bothRoles && addOn !== null
        ? t('subscription.billing.picker.breakdown', {
            plan: name,
            base: formatMoney(planPriceFor(plan, interval, false), currency, i18n.language),
            addOn: formatMoney(addOn, currency, i18n.language),
          })
        : null;
    const isZero = (plan.priceMinorUnits ?? 0) === 0;
    const offerNote = offer
      ? annual
        ? promotionAnnualNote(t, offer, formatMoney(fullPrice, currency, i18n.language), i18n.language)
        : promotionShortText(t, offer, i18n.language)
      : null;
    const note = isZero ? t('subscription.billing.picker.noCard') : [breakdown, offerNote].filter(Boolean).join(' · ') || undefined;
    // The bar spells out the full price after a limited promotion, which the monthly card leaves out.
    const barOffer = offer
      ? annual || offer.duration === PromotionDuration.Forever
        ? offerNote
        : t('subscription.promotion.card.then', {
            short: promotionShortText(t, offer, i18n.language),
            price: formatMoney(fullPrice, currency, i18n.language),
          })
      : null;
    const barNote = isZero ? t('subscription.billing.picker.noCard') : [breakdown, barOffer].filter(Boolean).join(' · ') || undefined;
    const barTitle = t('subscription.billing.select.planLine', {
      plan: name,
      price: formatMoney(offer ? (discounted ?? fullPrice) : fullPrice, currency, i18n.language),
      per: t(`subscription.planChoice.card.per.${annual ? 'year' : 'month'}`),
    });
    const secondRoleText =
      addOn !== null
        ? t('subscription.planChoice.card.secondRoleAddOn', { amount: formatMoney(addOn, currency, i18n.language) })
        : plan.bothRolesIncluded
          ? t('subscription.planChoice.card.bothRoles')
          : null;
    const higher = !free && planDisplayPrice(plan, bothRoles) > currentRank;

    const action = (() => {
      if (sameAsCurrent) {
        if (keep) {
          return {
            label: t('subscription.billing.cancelChange.keep', { plan: name }),
            primary: true,
            disabled: false,
            onClick: () => selection.keepCurrent(name),
          };
        }
        return { label: t('subscription.billing.picker.yourPlan'), primary: false, disabled: true, onClick: () => undefined };
      }
      if (free) {
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
      if (scheduledHere) {
        return {
          label: scheduledOn
            ? t('subscription.billing.picker.switchesTo', { plan: name, date: scheduledOn })
            : t('subscription.billing.picker.switchesToNoDate', { plan: name }),
          primary: false,
          disabled: true,
          onClick: () => undefined,
        };
      }
      // A coach paying monthly who moves to a yearly price, or the other way round, is changing the period, not the plan.
      const labelKey = higher
        ? annual
          ? 'subscription.billing.picker.upgradeAnnual'
          : 'subscription.planChoice.card.upgrade'
        : selection.live && annual && currentInterval === BillingInterval.Monthly
          ? 'subscription.billing.picker.switchToAnnual'
          : selection.live && !annual && currentInterval === BillingInterval.Annual
            ? 'subscription.billing.picker.switchToMonthly'
            : annual
              ? 'subscription.billing.picker.switchToAnnual'
              : 'subscription.billing.picker.switchTo';
      return {
        label: t(labelKey, { plan: name }),
        primary: higher,
        disabled: false,
        onClick: () =>
          selection.choosePaid(plan, bothRoles && secondRoleAddOnOf(plan) !== null, {
            billingInterval: interval,
            // A code only rides along with a checkout; a live subscription changes plan in place without one.
            code: selection.live ? undefined : (appliedCode ?? undefined),
          }),
      };
    })();

    // The current plan (unless its scheduled change can be cancelled) and a switch that is already scheduled cannot be picked.
    const selectable = sameAsCurrent ? cancellable : !(free && switchPending) && !scheduledHere;
    return {
      plan,
      name,
      action,
      current,
      keep,
      selectable,
      interval,
      annual,
      fullPrice,
      discounted,
      offer,
      note,
      barNote,
      barTitle,
      secondRoleText,
      higher,
      free,
    };
  });
  // A choice that stopped being selectable (it became the current plan, or its switch got scheduled) counts as none.
  const selected = cards.find((card) => card.selectable && card.plan.code === selectedCode);

  // A live paid subscription asks the server what a paid target would charge and when it starts.
  const selectedIsFree = selected?.free === true;
  const selectedIsKeep = selected?.keep === true;
  const previewQuery = usePlanChangePreview(
    selected && !selectedIsFree && !selectedIsKeep ? (selected.plan.code ?? null) : null,
    selected?.interval ?? BillingInterval.Monthly,
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

  const previewRefusal = previewQuery.isError ? billingErrorCode(previewQuery.error) : null;
  // The server cannot sell this plan on this period: nothing to confirm.
  const confirmBlocked = selection.live && previewRefusal === 'PLAN_NOT_PURCHASABLE';

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
    const planLabel = selected.name;
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
      if (previewRefusal === 'BILLING_NO_SUBSCRIPTION') return checkoutHint;
      if (previewRefusal === 'PLAN_NOT_PURCHASABLE') {
        return { hint: billingErrorMessage(previewQuery.error, 'subscription.billing.errors.preview') };
      }
      return { hint: t('subscription.billing.select.hintPreviewError') };
    }
    const preview = previewQuery.data;
    if (preview.timing === PlanChangeTiming.AtNextRenewal) {
      const date = formatDate(preview.nextChargeAt) ?? periodEndDate;
      // A paid year is not refunded: the switch waits for its end.
      if (preview.currentBillingInterval === BillingInterval.Annual && date) {
        return { hint: t('subscription.billing.select.hintStartsOnYearEnd', { plan: planLabel, date, current: currentName }) };
      }
      return startsOn(date);
    }
    const currency = preview.currency ?? 'CZK';
    const nextDate = formatDate(preview.nextChargeAt);
    const annual = preview.billingInterval === BillingInterval.Annual;
    // Both "Today about ..." hints are approximate, so they show whole crowns.
    const today = formatApproxMoney(preview.todayAmountMinorUnits ?? 0, currency, i18n.language);
    const extraHint = preview.cancelsScheduledSwitchToFree
      ? t('subscription.billing.select.hintCancelsFree', { free: freeName })
      : undefined;
    // Monthly to annual: the server credits the unused part of the month inside today's amount; the browser never works it out.
    if (annual && preview.currentBillingInterval === BillingInterval.Monthly) {
      return {
        hint: nextDate
          ? t('subscription.billing.select.hintMoveToAnnual', { today, plan: currentName, date: nextDate })
          : t('subscription.billing.select.hintMoveToAnnualNoDate', { today, plan: currentName }),
        extraHint,
      };
    }
    const per = t(`subscription.planChoice.card.per.${annual ? 'year' : 'month'}`);
    const span = t(`subscription.billing.select.${annual ? 'hintSpanYear' : 'hintSpanMonth'}`);
    const next = formatMoney(preview.nextAmountMinorUnits ?? 0, currency, i18n.language);
    return {
      hint: nextDate
        ? t('subscription.billing.select.hintUpgradeToday', { today, next, per, span, date: nextDate })
        : t('subscription.billing.select.hintUpgradeTodayNoDate', { today, next, per, span }),
      extraHint,
    };
  })();

  const rolesLine = bothRoles
    ? t('subscription.billing.picker.rolesBoth', { first: t('roles.trainer'), second: t('roles.nutritionist') })
    : t('subscription.billing.picker.rolesOne', { role: t(isNutritionist ? 'roles.nutritionist' : 'roles.trainer') });
  const onCheckoutPath = !selection.live;
  // Which period the coach is billed on now, spelled out while the switch shows the other one.
  const payingNote =
    selection.live && showSwitch && pageInterval !== currentInterval && currentName
      ? t(
          currentInterval === BillingInterval.Annual
            ? 'subscription.billing.picker.payingAnnual'
            : 'subscription.billing.picker.payingMonthly',
          { plan: currentName },
        )
      : null;
  const footer =
    pageInterval === BillingInterval.Annual
      ? t(selection.live ? 'subscription.billing.picker.footerAnnual' : 'subscription.billing.picker.footerAnnualNew')
      : t('subscription.billing.picker.footer');

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

      {promotion && <PromotionBanner promotion={promotion} plans={plans} variant="picker" interval={pageInterval} />}

      <div className="flex flex-wrap items-center gap-x-4 gap-y-1">
        <p className="text-copy text-muted-foreground">{rolesLine}</p>
        {onCheckoutPath && (
          <p className="ml-auto text-body text-muted-foreground">{t('subscription.billing.picker.codeNote')}</p>
        )}
      </div>

      {showSwitch && !plansQuery.isError && (
        <div className="flex flex-wrap items-center gap-3.5">
          <BillingIntervalSwitch
            value={pageInterval}
            savingPercent={maxAnnualSavingPercent(plans)}
            onChange={(next) => {
              setChosenInterval(next);
              // A refusal may have been about the other period; the next attempt starts clean.
              selection.clearCodeError();
            }}
          />
          {payingNote && <span className="text-body text-muted-foreground">{payingNote}</span>}
        </div>
      )}

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
          {cards.map(
            ({ plan, name, action, current, selectable, interval, annual, fullPrice, discounted, offer, note, secondRoleText }) => (
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
                originalPriceMinorUnits={offer && !annual ? fullPrice : undefined}
                billingInterval={interval}
                priceText={current ? formUpPriceText : undefined}
                priceNote={current && formUpPriceText ? undefined : note}
                secondRoleText={secondRoleText}
                onAction={action.onClick}
                selection={
                  selectable
                    ? {
                        selected: plan.code === selected?.plan.code,
                        onSelect: () => setSelectedCode(plan.code ?? null),
                        selectLabel: t('subscription.billing.select.select', { plan: name }),
                      }
                    : undefined
                }
              />
            ),
          )}
        </div>
      )}

      <TaxNote taxMode={plansQuery.data?.taxMode} />

      <p className="flex items-center gap-2.5 text-body text-muted-foreground">
        <CreditCard className="size-4 shrink-0" aria-hidden="true" />
        {footer}
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
          confirmDisabled={confirmBlocked}
          codeField={
            onCheckoutPath && !selected.free && !selected.keep ? (
              <PromotionCodeField
                appliedCode={appliedCode}
                error={selection.codeError}
                disabled={selection.busy}
                onApply={(code) => {
                  selection.clearCodeError();
                  setAppliedCode(code);
                }}
                onRemove={() => {
                  selection.clearCodeError();
                  setAppliedCode(null);
                }}
              />
            ) : undefined
          }
          onConfirm={selected.action.onClick}
        />
      )}
    </div>
  );
}
