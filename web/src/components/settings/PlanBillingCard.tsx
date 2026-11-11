import { useEffect, useRef, useState } from 'react';
import { useLocation } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Check, CreditCard, ExternalLink, Ticket } from 'lucide-react';
import {
  SecondRoleState,
  SubscriptionPriceLineKind,
  SubscriptionStatus,
  type SubscriptionFeaturesDto,
} from '@/api/generated';
import SettingsCard from '@/components/settings/SettingsCard';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { formatMoney } from '@/components/subscription/money';
import UsageBar from '@/components/subscription/UsageBar';
import { useSubscription } from '@/hooks/useSubscriptionQueries';
import { PLAN_BILLING_ANCHOR } from '@/components/subscription/planLinks';

const FEATURE_ROWS: readonly { flag: keyof SubscriptionFeaturesDto; labelKey: string }[] = [
  { flag: 'canCreatePlans', labelKey: 'subscription.plan.features.plans' },
  { flag: 'canMessage', labelKey: 'subscription.plan.features.messaging' },
  { flag: 'canSendQuestionnaires', labelKey: 'subscription.plan.features.forms' },
  { flag: 'canUseWeeklyCheckIns', labelKey: 'subscription.plan.features.checkIns' },
  { flag: 'canUsePerClientCheckInConfig', labelKey: 'subscription.plan.features.perClientCheckIns' },
  { flag: 'canUsePayments', labelKey: 'subscription.plan.features.payments' },
];

const STATUS_TONE: Record<SubscriptionStatus, string> = {
  [SubscriptionStatus.Trialing]: 'bg-success-soft text-success-ink',
  [SubscriptionStatus.Active]: 'bg-success-soft text-success-ink',
  [SubscriptionStatus.PastDue]: 'bg-error-soft text-error',
  [SubscriptionStatus.Canceled]: 'bg-sunken text-muted-foreground',
  [SubscriptionStatus.Incomplete]: 'bg-sunken text-muted-foreground',
};

/**
 * Settings section "Plan and billing": the coach's plan and status, the monthly bill, client usage
 * and included features. Plan changes, billing and codes stay disabled until plan selection exists.
 */
export default function PlanBillingCard() {
  const { t, i18n } = useTranslation();
  const { hash } = useLocation();
  const { query, isCoach, data, planName, clientLimit, activeClientCount } = useSubscription();
  const anchorRef = useRef<HTMLDivElement>(null);
  const [now] = useState(() => Date.now());
  const ready = query.isSuccess;

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
                <Button type="button" size="lg" className="px-3.5 font-semibold" disabled>
                  {t('subscription.plan.changePlan')}
                </Button>
                <Button type="button" variant="outline" size="lg" className="gap-1.75 px-3.5 font-semibold" disabled>
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
                      <div className="flex items-baseline gap-3 border-t border-border pt-2.5 text-subhead font-semibold text-ink">
                        <span>{t('subscription.plan.bill.total')}</span>
                        <span className="ml-auto whitespace-nowrap">
                          {t('subscription.plan.bill.perMonth', { amount: formatMoney(total, currency, language) })}
                        </span>
                      </div>
                    </>
                  )}
                </div>
                {data.secondRole === SecondRoleState.NotAvailable && (
                  <span className="text-meta text-muted-foreground">{t('subscription.plan.bill.secondRoleUnavailable')}</span>
                )}
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

            <div className="grid grid-cols-[minmax(0,1fr)_auto] items-end gap-3 border-t border-border pt-4.5">
              <div className="flex min-w-0 flex-col gap-1.75">
                <label htmlFor="plan-code" className="text-body font-semibold text-ink-2">
                  {t('subscription.plan.code.label')}
                </label>
                <span className="relative flex items-center">
                  <Ticket className="pointer-events-none absolute left-3 size-4 text-muted-foreground" aria-hidden="true" />
                  <Input
                    id="plan-code"
                    type="text"
                    disabled
                    placeholder={t('subscription.plan.code.placeholder')}
                    className="h-10 pl-9.5"
                  />
                </span>
                <span className="text-meta text-muted-foreground">{t('subscription.plan.code.hint')}</span>
              </div>
              <Button type="button" variant="outline" size="lg" className="mb-6.25 px-3.5 font-semibold" disabled>
                {t('subscription.plan.code.apply')}
              </Button>
            </div>

            <div className="flex items-center gap-4 border-t border-border pt-4.5">
              <div className="flex flex-col gap-1">
                <h3 className="text-copy font-semibold text-ink">{t('subscription.plan.card.title')}</h3>
                <span className="text-body text-muted-foreground">{t('subscription.plan.card.description')}</span>
              </div>
              <Button type="button" variant="ghost" size="lg" className="ml-auto gap-1 px-2 font-semibold" disabled>
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
