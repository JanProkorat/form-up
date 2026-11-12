import { useTranslation } from 'react-i18next';
import { Check, Minus, Users } from 'lucide-react';
import type { SubscriptionPlanDto } from '@/api/admin-subscription-plans';
import { formatPlanMoney, planName } from '@/components/admin-plans/planForm';
import type { PlanColor } from '@/components/admin-coaches/giveFreePlans';
import { ADMIN_FEATURE_LABEL_KEY } from '@/components/admin-plans/planFeatureLabels';
import { PLAN_FEATURES } from '@/components/subscription/planFeatures';
import { cn } from '@/lib/utils';

interface Props {
  plan: SubscriptionPlanDto;
  color: PlanColor;
  selected: boolean;
  /** The coach already has this plan: highlighted, and picking it only changes the end date. */
  current?: boolean;
  /** Radio group name, shared by every card of one picker. */
  name: string;
  onSelect: (code: string) => void;
}

/** The feature lines of a plan card, from the shared feature list. */
const FEATURES: ReadonlyArray<{ flag: keyof SubscriptionPlanDto; labelKey: string }> = PLAN_FEATURES.map((feature) => ({
  flag: feature.flag,
  labelKey: ADMIN_FEATURE_LABEL_KEY[feature.key],
}));

/** One selectable plan in the give-for-free picker: coloured header, regular price, limit and features. */
export default function AdminPlanCard({ plan, color, selected, current = false, name, onSelect }: Props) {
  const { t, i18n } = useTranslation();
  const code = plan.code ?? '';
  const Icon = color.icon;
  const cap = plan.maxActiveClients;
  const highlighted = selected || current;

  return (
    <label
      data-plan-card
      className={cn(
        'relative flex w-62.5 shrink-0 cursor-pointer snap-start flex-col gap-3 overflow-hidden rounded-xl border border-line bg-card p-4 shadow-panel focus-within:ring-3 focus-within:ring-ring/50',
        highlighted && ['border-2 p-[15px]', color.selected],
      )}
    >
      <span className={cn('-mx-4 -mt-4 flex flex-col gap-2 px-4 py-4', color.band)}>
        <span className="flex items-center gap-2.5">
          <input
            type="radio"
            name={name}
            value={code}
            checked={selected}
            onChange={() => onSelect(code)}
            className={cn('size-4.5 shrink-0 cursor-pointer', color.accent)}
          />
          <span className="min-w-0 truncate font-display text-card-title font-semibold text-ink">
            {planName(plan, i18n.language)}
          </span>
          {/* The pill shares the name row (no taller than the icon), so selecting a card never changes its height. */}
          {highlighted && (
            // A template string, not cn(): twMerge would drop the custom text-meta size next to text-ink.
            <span
              className={`shrink-0 rounded-full border px-2 py-0.5 text-meta font-semibold whitespace-nowrap text-ink ${color.pill}`}
            >
              {t(current ? 'adminCoaches.giveFree.current' : 'adminCoaches.giveFree.selected')}
            </span>
          )}
          <span
            aria-hidden="true"
            className={cn('ml-auto flex size-8 shrink-0 items-center justify-center rounded-field bg-card', color.tile)}
          >
            <Icon className="size-4" />
          </span>
        </span>
      </span>

      <span className="flex items-baseline gap-1.5">
        <span className="font-display text-stat font-semibold text-ink">
          {formatPlanMoney(plan.priceMinorUnits ?? 0, plan.currency, i18n.language)}
        </span>
        <span className="text-body text-muted-foreground">{t('adminCoaches.giveFree.perMonth')}</span>
      </span>

      <span className="flex flex-col gap-2 border-t border-line pt-3">
        <span className="flex items-center gap-2 text-copy font-semibold text-ink">
          <Users aria-hidden="true" className="size-4 text-muted-foreground" />
          {cap == null
            ? t('adminCoaches.giveFree.clientsUnlimited')
            : t('adminCoaches.giveFree.clientsUpTo', { count: cap })}
        </span>
        {FEATURES.map(({ flag, labelKey }) => {
          const included = plan[flag] === true;
          return (
            <span
              key={labelKey}
              className={`flex items-start gap-2 text-body ${included ? 'text-ink-2' : 'text-muted-foreground'}`}
            >
              {included ? (
                <Check aria-hidden="true" className={cn('mt-px size-4 shrink-0', color.tile)} />
              ) : (
                <Minus aria-hidden="true" className="mt-px size-4 shrink-0" />
              )}
              <span>
                {t(labelKey)}
                <span className="sr-only">
                  {included ? t('adminCoaches.giveFree.included') : t('adminCoaches.giveFree.notIncluded')}
                </span>
              </span>
            </span>
          );
        })}
      </span>
    </label>
  );
}
