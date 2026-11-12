import { useTranslation } from 'react-i18next';
import { ChevronDown, Filter, X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { ClientPlanFilter, type ClientPlanCounts } from '@/api/generated';
import { cn } from '@/lib/utils';

/** The "All plans" choice; the radio group needs a string value for it. */
const ALL_PLANS = 'All';

interface PlanOption {
  value: ClientPlanFilter | typeof ALL_PLANS;
  labelKey: string;
  count: (counts: ClientPlanCounts | undefined) => number | undefined;
}

const PLAN_OPTIONS: readonly PlanOption[] = [
  { value: ALL_PLANS, labelKey: 'clients.filterMenu.plans.all', count: (c) => c?.all },
  { value: ClientPlanFilter.Active, labelKey: 'clients.filterMenu.plans.active', count: (c) => c?.active },
  { value: ClientPlanFilter.None, labelKey: 'clients.filterMenu.plans.none', count: (c) => c?.none },
  { value: ClientPlanFilter.EndingSoon, labelKey: 'clients.filterMenu.plans.endingSoon', count: (c) => c?.endingSoon },
];

function labelKeyFor(plan: ClientPlanFilter): string {
  return PLAN_OPTIONS.find((option) => option.value === plan)?.labelKey ?? '';
}

interface MenuProps {
  /** The chosen plan; null is "All plans". */
  plan: ClientPlanFilter | null;
  /** Counts for the selected tab before the plan filter. An option at 0 is disabled unless chosen. */
  counts: ClientPlanCounts | undefined;
  onChange: (plan: ClientPlanFilter | null) => void;
}

/**
 * The "Filter" dropdown after the tag button: one single-choice "Plans" group
 * (All plans, Active plans, No plan, Plans ending soon), each with its count. Choosing
 * anything but "All plans" inverts the trigger and shows a "1" badge; the menu closes
 * on every choice.
 */
export default function ClientFilterMenu({ plan, counts, onChange }: MenuProps) {
  const { t } = useTranslation();
  const isActive = plan !== null;

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button
          type="button"
          variant="outline"
          className={cn(
            'h-9 gap-1.75 rounded-lg px-3.5 font-semibold',
            isActive ? 'border-primary bg-primary text-primary-foreground hover:bg-primary/90 hover:text-primary-foreground' : 'text-ink',
          )}
        >
          <Filter className="size-4" aria-hidden="true" />
          {t('clients.filterMenu.label')}
          {isActive && (
            <span className="inline-flex h-4.5 min-w-4.5 items-center justify-center rounded-full bg-primary-foreground px-1.25 text-label font-bold text-primary">
              1
            </span>
          )}
          <ChevronDown className="size-4" aria-hidden="true" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="w-64" aria-label={t('clients.filterMenu.label')}>
        <DropdownMenuLabel className="text-label font-bold tracking-label text-muted-foreground uppercase">
          {t('clients.filterMenu.plansGroup')}
        </DropdownMenuLabel>
        <DropdownMenuRadioGroup
          value={plan ?? ALL_PLANS}
          onValueChange={(value) => onChange(value === ALL_PLANS ? null : (value as ClientPlanFilter))}
        >
          {PLAN_OPTIONS.map(({ value, labelKey, count: countOf }) => {
            const chosen = (plan ?? ALL_PLANS) === value;
            const count = countOf(counts);
            return (
              <DropdownMenuRadioItem
                key={value}
                value={value}
                disabled={count === 0 && !chosen}
                className="gap-2.5 py-2 text-body font-medium"
              >
                {t(labelKey)}
                {typeof count === 'number' && (
                  <span className="ml-auto text-caption font-semibold text-muted-foreground">{count}</span>
                )}
              </DropdownMenuRadioItem>
            );
          })}
        </DropdownMenuRadioGroup>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

interface ActiveFiltersProps {
  plan: ClientPlanFilter | null;
  onRemove: () => void;
}

/** The removable chip for the chosen plan plus a "Clear filters" link. Renders nothing on "All plans". */
export function ClientActiveFilters({ plan, onRemove }: ActiveFiltersProps) {
  const { t } = useTranslation();

  if (plan === null) {
    return null;
  }

  const name = t(labelKeyFor(plan));

  return (
    <div className="flex flex-wrap items-center gap-2.5">
      <span className="inline-flex h-7 items-center gap-1.5 rounded-full bg-line py-0 pr-1.5 pl-2.5 text-caption font-semibold text-ink">
        {name}
        <button
          type="button"
          aria-label={t('clients.filterMenu.remove', { name })}
          onClick={onRemove}
          className="flex size-4.5 items-center justify-center rounded-full text-muted-foreground hover:text-ink focus-visible:outline-2 focus-visible:outline-ring"
        >
          <X className="size-3" aria-hidden="true" />
        </button>
      </span>
      <button
        type="button"
        onClick={onRemove}
        className="text-caption font-semibold text-muted-foreground hover:text-ink"
      >
        {t('clients.filterMenu.clearAll')}
      </button>
    </div>
  );
}
