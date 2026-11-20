import { useTranslation } from 'react-i18next';
import { BillingInterval } from '@/api/generated';

interface Props {
  value: BillingInterval;
  onChange: (interval: BillingInterval) => void;
  /** Largest annual saving in whole percent; the "Save up to N %" badge shows only when it is above zero. */
  savingPercent?: number | null;
  /** Extra classes for the placement on the page. */
  className?: string;
}

const OPTIONS = [
  { interval: BillingInterval.Monthly, label: 'subscription.billing.interval.monthly' },
  { interval: BillingInterval.Annual, label: 'subscription.billing.interval.annual' },
] as const;

/** Monthly | Annual switch above the plan cards. Show it only when some plan is sold annually. */
export default function BillingIntervalSwitch({ value, onChange, savingPercent, className = '' }: Props) {
  const { t } = useTranslation();

  return (
    <div
      role="group"
      aria-label={t('subscription.billing.interval.label')}
      className={`inline-flex gap-1 rounded-xl border border-line bg-sunken p-1 ${className}`}
    >
      {OPTIONS.map(({ interval, label }) => {
        const active = value === interval;
        const badge = interval === BillingInterval.Annual && savingPercent !== null && savingPercent !== undefined && savingPercent > 0;
        return (
          <button
            key={interval}
            type="button"
            aria-pressed={active}
            onClick={() => onChange(interval)}
            className={`inline-flex h-10 items-center gap-2 rounded-lg px-4 text-copy font-semibold transition-colors ${active ? 'bg-primary text-primary-foreground' : 'text-ink-2 hover:text-ink'}`}
          >
            {t(label)}
            {badge && (
              <span className="inline-flex h-5.5 items-center rounded-full bg-success-soft px-2 text-meta font-semibold whitespace-nowrap text-success-ink">
                {t('subscription.billing.interval.save', { percent: savingPercent })}
              </span>
            )}
          </button>
        );
      })}
    </div>
  );
}
