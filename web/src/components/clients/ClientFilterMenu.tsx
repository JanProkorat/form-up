import { useTranslation } from 'react-i18next';
import { ChevronDown, Clock, Filter, Lock, X, type LucideIcon } from 'lucide-react';
import { Button } from '@/components/ui/button';
import {
  DropdownMenu,
  DropdownMenuCheckboxItem,
  DropdownMenuContent,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import type { ClientListFlag } from '@/hooks/useClientListParams';
import { cn } from '@/lib/utils';

interface FlagDefinition {
  flag: ClientListFlag;
  icon: LucideIcon;
  labelKey: string;
}

const FLAGS: readonly FlagDefinition[] = [
  { flag: 'endingSoon', icon: Clock, labelKey: 'clients.filterMenu.endingSoon' },
  { flag: 'outsidePlan', icon: Lock, labelKey: 'clients.filterMenu.outsidePlan' },
];

type FlagState = Record<ClientListFlag, boolean>;
type FlagCounts = Partial<Record<ClientListFlag, number>>;

interface MenuProps {
  active: FlagState;
  /** Per-flag counts; a flag whose count is 0 is disabled unless it is on, and "outsidePlan" is hidden at 0. */
  counts: FlagCounts;
  /** Hides the "Outside plan" row (the Archived tab: the backend rejects the pair). */
  outsidePlanAvailable: boolean;
  onToggle: (flag: ClientListFlag, on: boolean) => void;
}

function isRowShown(flag: ClientListFlag, props: MenuProps): boolean {
  if (flag !== 'outsidePlan') {
    return true;
  }
  return props.outsidePlanAvailable && ((props.counts.outsidePlan ?? 0) > 0 || props.active.outsidePlan);
}

/**
 * The "Filter" dropdown after the tag button: one checkbox row per independent flag
 * (Plans ending soon, Outside plan), each with an icon and its count. With a flag on,
 * the trigger inverts and shows how many are on.
 */
export default function ClientFilterMenu(props: MenuProps) {
  const { t } = useTranslation();
  const { active, onToggle } = props;
  const activeCount = FLAGS.filter(({ flag }) => active[flag]).length;
  const isActive = activeCount > 0;

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
              {activeCount}
            </span>
          )}
          <ChevronDown className="size-4" aria-hidden="true" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="w-64" aria-label={t('clients.filterMenu.label')}>
        {FLAGS.filter(({ flag }) => isRowShown(flag, props)).map(({ flag, icon: Icon, labelKey }) => {
          const checked = active[flag];
          const count = props.counts[flag];
          return (
            <DropdownMenuCheckboxItem
              key={flag}
              checked={checked}
              disabled={count === 0 && !checked}
              // Keep the menu open so both flags can be switched in one go.
              onSelect={(event) => event.preventDefault()}
              onCheckedChange={(on) => onToggle(flag, on === true)}
              className="gap-2.5 py-2 text-body font-medium"
            >
              <Icon className="size-3.5 text-muted-foreground" aria-hidden="true" />
              {t(labelKey)}
              {typeof count === 'number' && (
                <span className="ml-auto text-caption font-semibold text-muted-foreground">{count}</span>
              )}
            </DropdownMenuCheckboxItem>
          );
        })}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

interface ActiveFiltersProps {
  active: FlagState;
  onRemove: (flag: ClientListFlag) => void;
  onClearAll: () => void;
}

/** One removable chip per flag that is on, plus a "Clear filters" link. Renders nothing with no flag on. */
export function ClientActiveFilters({ active, onRemove, onClearAll }: ActiveFiltersProps) {
  const { t } = useTranslation();
  const on = FLAGS.filter(({ flag }) => active[flag]);

  if (on.length === 0) {
    return null;
  }

  return (
    <div className="flex flex-wrap items-center gap-2.5">
      {on.map(({ flag, icon: Icon, labelKey }) => {
        const name = t(labelKey);
        return (
          <span
            key={flag}
            className="inline-flex h-7 items-center gap-1.5 rounded-full bg-line py-0 pr-1.5 pl-2.5 text-caption font-semibold text-ink"
          >
            <Icon className="size-3" aria-hidden="true" />
            {name}
            <button
              type="button"
              aria-label={t('clients.filterMenu.remove', { name })}
              onClick={() => onRemove(flag)}
              className="flex size-4.5 items-center justify-center rounded-full text-muted-foreground hover:text-ink focus-visible:outline-2 focus-visible:outline-ring"
            >
              <X className="size-3" aria-hidden="true" />
            </button>
          </span>
        );
      })}
      <button
        type="button"
        onClick={onClearAll}
        className="text-caption font-semibold text-muted-foreground hover:text-ink"
      >
        {t('clients.filterMenu.clearAll')}
      </button>
    </div>
  );
}
