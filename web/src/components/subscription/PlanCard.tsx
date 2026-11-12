import { useTranslation } from 'react-i18next';
import { Check, Users, X } from 'lucide-react';
import { BillingInterval, type CoachPlanDto } from '@/api/generated';
import { formatMoney } from '@/components/subscription/money';
import { coachPlanName } from '@/components/subscription/planNames';
import { Button } from '@/components/ui/button';

interface Props {
  plan: CoachPlanDto;
  /** Marks the plan the coach is on now. */
  current?: boolean;
  /** Button text, already translated. */
  actionLabel: string;
  /** Dark primary button when true, outline otherwise. */
  primary: boolean;
  /** Plans that cannot be taken yet (no billing) keep a disabled button. */
  actionDisabled: boolean;
  busy: boolean;
  onAction: () => void;
}

const FEATURES = [
  { flag: 'canCreatePlans', key: 'plans' },
  { flag: 'canMessage', key: 'messaging' },
  { flag: 'canSendQuestionnaires', key: 'forms' },
  { flag: 'canUseWeeklyCheckIns', key: 'checkIns' },
  { flag: 'canUsePerClientCheckInConfig', key: 'perClientCheckIns' },
  { flag: 'canUsePayments', key: 'payments' },
] as const;

/** One plan of the picker: name, price, second-role line, client cap, included features and the choose button. */
export default function PlanCard({ plan, current = false, actionLabel, primary, actionDisabled, busy, onAction }: Props) {
  const { t, i18n } = useTranslation();
  const name = coachPlanName(plan, i18n.language);
  const currency = plan.currency ?? 'CZK';
  const price = formatMoney(plan.priceMinorUnits ?? 0, currency, i18n.language);
  const interval = plan.billingInterval === BillingInterval.Annual ? 'year' : 'month';
  const addOn = plan.secondRoleAddOnPriceMinorUnits;
  const secondRole =
    addOn !== undefined && addOn !== null
      ? t('subscription.planChoice.card.secondRoleAddOn', { amount: formatMoney(addOn, currency, i18n.language) })
      : plan.bothRolesIncluded
        ? t('subscription.planChoice.card.bothRoles')
        : null;
  const cap = plan.maxActiveClients;

  return (
    <article
      aria-label={name}
      className="flex flex-col gap-4 rounded-2xl border border-line bg-card p-5 shadow-panel"
    >
      <div className="flex items-center gap-2">
        <h2 className="text-card-title font-semibold text-ink">{name}</h2>
        {current && (
          <span className="rounded-full bg-sunken px-2.5 py-0.5 text-body font-semibold text-ink-2">
            {t('subscription.planChoice.card.current')}
          </span>
        )}
      </div>

      <div className="flex items-baseline gap-2">
        <span className="text-stat font-semibold text-ink">{price}</span>
        <span className="text-body text-muted-foreground">{t(`subscription.planChoice.card.per.${interval}`)}</span>
      </div>

      {/* Two lines are reserved for the price note so the buttons line up across the cards. */}
      <div className="min-h-18">
        {secondRole && (
          <div className="flex items-center gap-2 rounded-lg bg-sunken px-3 py-2.5 text-body text-ink-2">
            <Users className="size-4 shrink-0" aria-hidden="true" />
            <span>{secondRole}</span>
          </div>
        )}
      </div>

      <Button
        type="button"
        variant={primary ? 'default' : 'outline'}
        size="lg"
        className="px-3.5 font-semibold"
        disabled={actionDisabled || busy}
        onClick={onAction}
      >
        {actionLabel}
      </Button>

      <div className="h-px bg-line" />

      <div className="flex items-center gap-2 text-subhead font-semibold text-ink">
        <Users className="size-4.5 shrink-0" aria-hidden="true" />
        {cap === undefined || cap === null
          ? t('subscription.planChoice.card.unlimitedClients')
          : t('subscription.planChoice.card.clientCap', { count: cap })}
      </div>

      <ul className="flex flex-col gap-2.5">
        {FEATURES.map(({ flag, key }) => {
          const included = plan[flag] === true;
          return (
            <li key={key} className="flex items-start gap-2.5 text-body">
              {included ? (
                <Check className="mt-0.5 size-3.5 shrink-0 text-success" aria-hidden="true" />
              ) : (
                <X className="mt-0.5 size-3.5 shrink-0 text-error" aria-hidden="true" />
              )}
              {/* Plain templates, not cn(): twMerge would drop text-body next to a text colour class. */}
              <span className={included ? 'text-ink-2' : 'text-muted-foreground'}>
                {!included && <span className="sr-only">{t('subscription.planChoice.card.notIncluded')} </span>}
                {t(`subscription.plan.features.${key}`)}
              </span>
            </li>
          );
        })}
      </ul>
    </article>
  );
}
