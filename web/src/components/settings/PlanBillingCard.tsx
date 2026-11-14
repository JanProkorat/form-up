import { useEffect, useRef, useState } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Check, CreditCard, ExternalLink, Ticket } from 'lucide-react';
import { monthsUntil, promotionValueText } from '@/components/subscription/promotionText';
import {
  PromotionDuration,
  SecondRoleState,
  SubscriptionPriceLineKind,
  SubscriptionStatus,
  type SubscriptionFeaturesDto,
} from '@/api/generated';
import SettingsCard from '@/components/settings/SettingsCard';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { billingErrorMessage, showBillingError } from '@/components/subscription/billingErrors';
import { formatMoney } from '@/components/subscription/money';
import TaxNote from '@/components/subscription/TaxNote';
import UsageBar from '@/components/subscription/UsageBar';
import { useApplyPromotionCode, useOpenBillingPortal, useSubscription } from '@/hooks/useSubscriptionQueries';
import { PLAN_BILLING_ANCHOR, PLAN_PICKER_PATH } from '@/components/subscription/planLinks';
import { useToastStore } from '@/stores/toast';
import { PLAN_FEATURES, coachFeatureLabelKey } from '@/components/subscription/planFeatures';

const FEATURE_ROWS: readonly { flag: keyof SubscriptionFeaturesDto; labelKey: string }[] = PLAN_FEATURES.map(
  (feature) => ({ flag: feature.flag, labelKey: coachFeatureLabelKey(feature.key) }),
);

const STATUS_TONE: Record<SubscriptionStatus, string> = {
  [SubscriptionStatus.Trialing]: 'bg-success-soft text-success-ink',
  [SubscriptionStatus.Active]: 'bg-success-soft text-success-ink',
  [SubscriptionStatus.PastDue]: 'bg-error-soft text-error',
  [SubscriptionStatus.Canceled]: 'bg-sunken text-muted-foreground',
  [SubscriptionStatus.Incomplete]: 'bg-sunken text-muted-foreground',
};

/**
 * Settings section "Plan and billing": the coach's plan and status, the monthly bill, client usage
 * and included features, plus the way to change plan, apply a code and manage billing in Stripe.
 */
export default function PlanBillingCard() {
  const { t, i18n } = useTranslation();
  const { hash } = useLocation();
  const { query, isCoach, data, planName, clientLimit, activeClientCount } = useSubscription();
  const anchorRef = useRef<HTMLDivElement>(null);
  const [now] = useState(() => Date.now());
  const ready = query.isSuccess;
  const portal = useOpenBillingPortal();
  const applyCode = useApplyPromotionCode();
  const [code, setCode] = useState('');
  const [codeError, setCodeError] = useState<string | null>(null);
  const canOpenPortal = data?.hasBillingAccount === true;

  const openPortal = () =>
    portal.mutate(undefined, { onError: (error) => showBillingError(error, 'subscription.billing.errors.portal') });

  const submitCode = () => {
    const trimmed = code.trim();
    if (trimmed === '' || applyCode.isPending) return;
    applyCode.mutate(trimmed, {
      onSuccess: () => {
        setCode('');
        setCodeError(null);
        useToastStore.getState().addToast(t('subscription.billing.code.applied'), 'success');
      },
      // Inline, with the field left as typed so a typo can be fixed in place.
      onError: (error) => setCodeError(billingErrorMessage(error, 'subscription.billing.errors.code')),
    });
  };

  // The account menu and the sidebar row link here with #plan-billing; scroll once the card has rendered.
  useEffect(() => {
    if (ready && hash === `#${PLAN_BILLING_ANCHOR}`) {
      anchorRef.current?.scrollIntoView({ block: 'start' });
    }
  }, [ready, hash]);

  if (!isCoach) return null;

  const language = i18n.language;
  const dateFormat = new Intl.DateTimeFormat(language, { dateStyle: 'medium' });
  const status = data?.status;
  const periodEnd = data?.currentPeriodEndsAt ? dateFormat.format(new Date(data.currentPeriodEndsAt)) : null;
  const freeUntil =
    data?.freeUntil && new Date(data.freeUntil).getTime() > now ? dateFormat.format(new Date(data.freeUntil)) : null;
  const subtitle = [
    periodEnd
      ? t(data?.cancelAtPeriodEnd ? 'subscription.plan.ends' : 'subscription.plan.renews', { date: periodEnd })
      : null,
    data?.isSetByFormUp
      ? freeUntil
        ? t('subscription.plan.setByFormUpUntil', { date: freeUntil })
        : t('subscription.plan.setByFormUp')
      : freeUntil
        ? t('subscription.plan.freeUntil', { date: freeUntil })
        : null,
  ]
    .filter(Boolean)
    .join(' · ');

  const lines = data?.priceLines ?? [];
  const currency = lines[0]?.currency ?? '';
  const total = lines.reduce((sum, line) => sum + (line.amountMinorUnits ?? 0), 0);
  const promotion = data?.promotion;
  const offerValue = promotion ? promotionValueText(t, promotion, language) : '';
  const offerName = promotion?.name ?? '';
  const offerLabel = !promotion
    ? ''
    : promotion.duration === PromotionDuration.Months && promotion.endsAt
      ? t('subscription.plan.bill.offer.months', {
          name: offerName,
          value: offerValue,
          left: Math.min(monthsUntil(promotion.endsAt, now), promotion.durationMonths ?? 0),
          count: promotion.durationMonths ?? 0,
        })
      : promotion.duration === PromotionDuration.FirstMonth
        ? t('subscription.plan.bill.offer.firstMonth', { name: offerName, value: offerValue })
        : t('subscription.plan.bill.offer.forever', { name: offerName, value: offerValue });
  const includedFeatures = FEATURE_ROWS.filter((row) => data?.features?.[row.flag] === true);
  const full = clientLimit !== null && activeClientCount >= clientLimit;

  return (
    <div id={PLAN_BILLING_ANCHOR} ref={anchorRef}>
      <SettingsCard title={t('subscription.plan.title')} description={t('subscription.plan.description')}>
        {query.isPending ? (
          <Skeleton className="h-64 w-full rounded-lg" />
        ) : query.isError || !data ? (
          <p role="alert" className="text-body text-error">
            {t('subscription.plan.loadError')}
          </p>
        ) : (
          <>
            <div className="flex flex-wrap items-center gap-3.5">
              <span className="flex size-11 shrink-0 items-center justify-center rounded-md bg-training-soft text-training-ink">
                <CreditCard className="size-5" aria-hidden="true" />
              </span>
              <div className="flex flex-col gap-1">
                <div className="flex items-center gap-2.5">
                  <h3 className="font-display text-card-title font-semibold text-ink">{planName}</h3>
                  {status && (
                    <span
                      className={`inline-flex h-5.5 items-center gap-1.25 rounded-full px-2 text-label font-semibold whitespace-nowrap ${STATUS_TONE[status]}`}
                    >
                      <span className="size-1.5 rounded-full bg-current" aria-hidden="true" />
                      {t(`subscription.status.${status}`)}
                    </span>
                  )}
                </div>
                {subtitle && <span className="text-body text-muted-foreground">{subtitle}</span>}
              </div>
              <span className="ml-auto flex items-center gap-2">
                <Button asChild size="lg" className="px-3.5 font-semibold">
                  <Link to={PLAN_PICKER_PATH}>{t('subscription.plan.changePlan')}</Link>
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  size="lg"
                  className="gap-1.75 px-3.5 font-semibold"
                  disabled={!canOpenPortal || portal.isPending}
                  onClick={openPortal}
                >
                  <CreditCard aria-hidden="true" />
                  {t('subscription.plan.manageBilling')}
                </Button>
              </span>
            </div>

            <div className="grid gap-6 md:grid-cols-2">
              <div className="flex flex-col gap-2.5">
                <h3 className="text-copy font-semibold text-ink">{t('subscription.plan.bill.title')}</h3>
                <div className="flex flex-col gap-2.5 rounded-xl bg-sunken p-4">
                  {lines.length === 0 ? (
                    <span className="text-copy text-ink-2">{t('subscription.plan.bill.free')}</span>
                  ) : (
                    <>
                      {lines.map((line) => (
                        <div key={line.kind} className="flex items-baseline gap-3 text-copy text-ink-2">
                          <span>
                            {line.kind === SubscriptionPriceLineKind.SecondRoleAddOn
                              ? t('subscription.plan.bill.secondRole')
                              : t('subscription.plan.bill.plan', { plan: planName })}
                          </span>
                          <span className="ml-auto font-semibold whitespace-nowrap">
                            {formatMoney(line.amountMinorUnits ?? 0, line.currency ?? currency, language)}
                          </span>
                        </div>
                      ))}
                      {promotion && (
                        <div className="flex items-baseline gap-3 text-copy text-success-ink">
                          <span>{offerLabel}</span>
                          <span className="ml-auto font-semibold whitespace-nowrap">{offerValue}</span>
                        </div>
                      )}
                      <div className="flex items-baseline gap-3 border-t border-border pt-2.5 text-subhead font-semibold text-ink">
                        <span>{t(promotion ? 'subscription.plan.bill.fullPrice' : 'subscription.plan.bill.total')}</span>
                        <span className="ml-auto whitespace-nowrap">
                          {t('subscription.plan.bill.perMonth', { amount: formatMoney(total, currency, language) })}
                        </span>
                      </div>
                      {promotion && promotion.endsAt && (
                        <span className="text-meta text-muted-foreground">
                          {t('subscription.plan.bill.afterOffer', {
                            date: dateFormat.format(new Date(promotion.endsAt)),
                            amount: formatMoney(total, currency, language),
                          })}
                        </span>
                      )}
                    </>
                  )}
                </div>
                {data.secondRole === SecondRoleState.NotAvailable && (
                  <span className="text-meta text-muted-foreground">{t('subscription.plan.bill.secondRoleUnavailable')}</span>
                )}
                {lines.length > 0 && <TaxNote taxMode={data.taxMode} className="text-meta text-muted-foreground" />}
              </div>

              <div className="flex flex-col gap-2.5">
                <h3 className="text-copy font-semibold text-ink">{t('subscription.plan.clients.title')}</h3>
                <div className="flex flex-col gap-2.5 rounded-xl bg-sunken p-4">
                  <span className="flex items-baseline gap-1.5">
                    {/* Plain template, not cn(): twMerge would drop text-stat next to a text colour class. */}
                    <span className={`font-display text-stat font-semibold ${full ? 'text-error' : 'text-ink'}`}>
                      {activeClientCount}
                    </span>
                    <span className="text-copy text-muted-foreground">
                      {clientLimit === null
                        ? t('subscription.plan.clients.unlimited')
                        : t('subscription.plan.clients.ofLimit', { limit: clientLimit })}
                    </span>
                  </span>
                  <UsageBar used={activeClientCount} limit={clientLimit} trackClassName="bg-border" />
                  <span className="text-meta text-muted-foreground">{t('subscription.plan.clients.countsOnce')}</span>
                </div>
                <ul className="flex flex-col gap-2">
                  {includedFeatures.map((row) => (
                    <li key={row.flag} className="flex gap-2 text-body text-ink-2">
                      <Check className="size-4 shrink-0 text-success" aria-hidden="true" />
                      {t(row.labelKey)}
                    </li>
                  ))}
                </ul>
              </div>
            </div>

            <form
              className="grid grid-cols-[minmax(0,1fr)_auto] items-end gap-3 border-t border-border pt-4.5"
              onSubmit={(event) => {
                event.preventDefault();
                submitCode();
              }}
            >
              <div className="flex min-w-0 flex-col gap-1.75">
                <label htmlFor="plan-code" className="text-body font-semibold text-ink-2">
                  {t('subscription.plan.code.label')}
                </label>
                <span className="relative flex items-center">
                  <Ticket className="pointer-events-none absolute left-3 size-4 text-muted-foreground" aria-hidden="true" />
                  <Input
                    id="plan-code"
                    type="text"
                    value={code}
                    autoComplete="off"
                    spellCheck={false}
                    aria-invalid={codeError !== null}
                    aria-describedby="plan-code-note"
                    placeholder={t('subscription.plan.code.placeholder')}
                    className="h-10 pl-9.5"
                    onChange={(event) => {
                      setCode(event.target.value);
                      setCodeError(null);
                    }}
                  />
                </span>
                {codeError ? (
                  <span id="plan-code-note" role="alert" className="text-meta text-error">
                    {codeError}
                  </span>
                ) : (
                  <span id="plan-code-note" className="text-meta text-muted-foreground">
                    {t('subscription.plan.code.hint')}
                  </span>
                )}
              </div>
              <Button
                type="submit"
                variant="outline"
                size="lg"
                className="mb-6.25 px-3.5 font-semibold"
                disabled={code.trim() === '' || applyCode.isPending}
              >
                {t('subscription.plan.code.apply')}
              </Button>
            </form>

            <div className="flex items-center gap-4 border-t border-border pt-4.5">
              <div className="flex flex-col gap-1">
                <h3 className="text-copy font-semibold text-ink">{t('subscription.plan.card.title')}</h3>
                <span className="text-body text-muted-foreground">
                  {t(canOpenPortal ? 'subscription.plan.card.description' : 'subscription.plan.card.descriptionFree')}
                </span>
              </div>
              <Button
                type="button"
                variant="ghost"
                size="lg"
                className="ml-auto gap-1 px-2 font-semibold"
                disabled={!canOpenPortal || portal.isPending}
                onClick={openPortal}
              >
                {t('subscription.plan.manageBilling')}
                <ExternalLink aria-hidden="true" />
              </Button>
            </div>
          </>
        )}
      </SettingsCard>
    </div>
  );
}
