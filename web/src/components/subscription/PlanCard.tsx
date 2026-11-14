import { useTranslation } from 'react-i18next';
import { Check, Users, X } from 'lucide-react';
import { BillingInterval, type CoachPlanDto } from '@/api/generated';
import { formatMoney } from '@/components/subscription/money';
import { coachPlanName } from '@/components/subscription/planNames';
import { PLAN_FEATURES } from '@/components/subscription/planFeatures';
import { Button } from '@/components/ui/button';

interface Props {
  plan: CoachPlanDto;
  /** Marks the plan the coach is on now. */
  current?: boolean;
  /** Button text, already translated. */
  actionLabel: string;
  /** Dark primary button when true, outline otherwise. */
  primary: boolean;
  /** Disables the button, for the plan the coach is on and for plans that cannot be taken right now. */
  actionDisabled: boolean;
  busy: boolean;
  onAction: () => void;
  /** Price to show instead of the plan's own, for example with the second-role add-on included. */
  priceMinorUnits?: number;
  /** Full price to show crossed out next to a discounted `priceMinorUnits`; omitted when nothing is discounted. */
  originalPriceMinorUnits?: number;
  /** Small line under the price, already translated. */
  priceNote?: string;
  /** Second-role line, already translated; null hides it, undefined derives it from the plan. */
  secondRoleText?: string | null;
  /** Pill next to the plan name, already translated; defaults to the "Your plan now" pill for the current plan. */
  badge?: string;
  /** Heavy outline: the plan the coach is on, or the one preselected. Defaults to `current`. */
  emphasized?: boolean;
}

const FEATURES = PLAN_FEATURES;

/** One plan of the picker: name, price, second-role line, client cap, included features and the choose button. */
export default function PlanCard({
  plan,
  current = false,
  actionLabel,
  primary,
  actionDisabled,
  busy,
  onAction,
  priceMinorUnits,
  originalPriceMinorUnits,
  priceNote,
  secondRoleText,
  badge,
  emphasized,
}: Props) {
  const { t, i18n } = useTranslation();
  const name = coachPlanName(plan, i18n.language);
  const currency = plan.currency ?? 'CZK';
  const price = formatMoney(priceMinorUnits ?? plan.priceMinorUnits ?? 0, currency, i18n.language);
  const originalPrice =
    originalPriceMinorUnits === undefined ? null : formatMoney(originalPriceMinorUnits, currency, i18n.language);
  const interval = plan.billingInterval === BillingInterval.Annual ? 'year' : 'month';
  const addOn = plan.secondRoleAddOnPriceMinorUnits;
  const derivedSecondRole =
    addOn !== undefined && addOn !== null
      ? t('subscription.planChoice.card.secondRoleAddOn', { amount: formatMoney(addOn, currency, i18n.language) })
      : plan.bothRolesIncluded
        ? t('subscription.planChoice.card.bothRoles')
        : null;
  const secondRole = secondRoleText === undefined ? derivedSecondRole : secondRoleText;
  const pill = badge ?? (current ? t('subscription.planChoice.card.current') : null);
  const heavy = emphasized ?? current;
  const cap = plan.maxActiveClients;

  return (
    <article
      aria-label={name}
      className={`flex flex-col gap-4 rounded-2xl bg-card p-5 shadow-panel ${heavy ? 'border-2 border-ink' : 'border border-line'}`}
    >
      <div className="flex items-center gap-2">
        <h2 className="text-card-title font-semibold text-ink">{name}</h2>
        {pill && (
          <span className="ml-auto rounded-full bg-sunken px-2.5 py-0.5 text-body font-semibold text-ink-2">{pill}</span>
        )}
      </div>

      <div className="flex flex-col gap-1">
        <div className="flex items-baseline gap-2">
          <span className="text-stat font-semibold text-ink">{price}</span>
          {originalPrice && (
            <s className="text-copy text-muted-foreground">
              <span className="sr-only">{t('subscription.promotion.card.regularPrice')} </span>
              {originalPrice}
            </s>
          )}
          <span className="text-body text-muted-foreground">{t(`subscription.planChoice.card.per.${interval}`)}</span>
        </div>
        {/* Two lines are reserved for the note so the buttons line up across the cards. */}
        <span className="min-h-9 text-meta text-muted-foreground">{priceNote}</span>
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
