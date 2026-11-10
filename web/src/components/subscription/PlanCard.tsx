import { useTranslation } from 'react-i18next';
import { Check, Minus, Users } from 'lucide-react';
import { BillingInterval, type CoachPlanDto } from '@/api/generated';
import { formatMoney } from '@/components/subscription/money';
import { Button } from '@/components/ui/button';

interface Props {
  plan: CoachPlanDto;
  /** Free is the only plan a coach can take until billing exists. */
  selectable: boolean;
  busy: boolean;
  onChoose: () => void;
}

const FEATURES = [
  { flag: 'canCreatePlans', key: 'plans' },
  { flag: 'canMessage', key: 'messaging' },
  { flag: 'canSendQuestionnaires', key: 'forms' },
  { flag: 'canUseWeeklyCheckIns', key: 'checkIns' },
  { flag: 'canUsePerClientCheckInConfig', key: 'perClientCheckIns' },
  { flag: 'canUsePayments', key: 'payments' },
] as const;

function planNameFor(plan: CoachPlanDto, language: string): string {
  const lang = language.slice(0, 2).toLowerCase();
  const byLang = lang === 'cs' ? plan.nameCs : lang === 'de' ? plan.nameDe : plan.nameEn;
  return byLang || plan.nameEn || plan.nameCs || plan.nameDe || plan.code || '';
}

/** One plan of the picker: name, price, second-role line, client cap, included features and the choose button. */
export default function PlanCard({ plan, selectable, busy, onChoose }: Props) {
  const { t, i18n } = useTranslation();
  const name = planNameFor(plan, i18n.language);
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
      <h2 className="text-card-title font-semibold text-ink">{name}</h2>

      <div className="flex items-baseline gap-2">
        <span className="text-stat font-semibold text-ink">{price}</span>
        <span className="text-body text-muted-foreground">{t(`subscription.planChoice.card.per.${interval}`)}</span>
      </div>

      {secondRole && (
        <div className="flex items-center gap-2 rounded-lg bg-sunken px-3 py-2.5 text-body text-ink-2">
          <Users className="size-4 shrink-0" aria-hidden="true" />
          <span>{secondRole}</span>
        </div>
      )}

      <Button
        type="button"
        variant={selectable ? 'default' : 'outline'}
        size="lg"
        className="px-3.5 font-semibold"
        disabled={!selectable || busy}
        onClick={onChoose}
      >
        {selectable ? t('subscription.planChoice.card.choose', { plan: name }) : t('subscription.planChoice.card.soon')}
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
                <Minus className="mt-0.5 size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
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
