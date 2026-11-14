import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Lock, Plus, Search } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { useAuthStore } from '@/stores/auth';
import { getErrorStatus } from '@/lib/api-errors';
import { isSubscriptionRequired } from '@/lib/subscriptionErrors';
import { useSubscription } from '@/hooks/useSubscriptionQueries';
import PaywallPanel from '@/components/subscription/PaywallPanel';
import { usePlanTemplateListParams } from '@/hooks/usePlanTemplateListParams';
import { usePlanTemplates } from '@/hooks/usePlanTemplatesQueries';
import { useDebouncedValue } from '@/hooks/useDebouncedValue';
import PageHeader from '@/components/library/PageHeader';
import Pagination from '@/components/library/Pagination';
import PlanTemplatesTable from '@/components/plan-templates/PlanTemplatesTable';
import NewPlanTemplateSheet from '@/components/plan-templates/NewPlanTemplateSheet';
import SingleOptionFilterPopover from '@/components/plan-templates/SingleOptionFilterPopover';
import { GOAL_ORDER } from '@/components/plan-templates/plan-template-tree';

const WEEK_FILTER_VALUES = [1, 2, 4, 6, 8, 12];
const MEALS_FILTER_VALUES = [2, 3, 4, 5, 6];
type InUseValue = 'yes' | 'no';

/**
 * The Plan templates list — search, Goal / Length / Meals per day / In use
 * filters, the templates table, pagination and the New template drawer.
 * Mirrors `RecipesPage`; the endpoints are nutritionist-only, so a trainer-only
 * coach sees an explanatory state instead of a failed load.
 */
export default function PlanTemplatesPage() {
  const { t } = useTranslation();
  const { filters, setSearch, setGoal, setWeekCount, setMealsPerDay, setInUse, setPage, clearFilters } =
    usePlanTemplateListParams();
  const roles = useAuthStore((s) => s.user?.roles ?? []);
  const isNutritionist = roles.includes('Nutritionist');

  const [searchInput, setSearchInput] = useState(filters.search);
  const [sheetOpen, setSheetOpen] = useState(false);

  // Re-sync the local input when the URL's search value changes elsewhere (back/forward, clearFilters).
  const [syncedSearch, setSyncedSearch] = useState(filters.search);
  if (filters.search !== syncedSearch) {
    setSyncedSearch(filters.search);
    setSearchInput(filters.search);
  }

  useDebouncedValue(searchInput, 300, () => {
    if (searchInput !== filters.search) {
      setSearch(searchInput);
    }
  });

  const { isEntitled } = useSubscription();
  const planAllows = isEntitled('canUsePlanTemplates');
  const templatesQuery = usePlanTemplates(filters, isNutritionist && planAllows);
  // isEntitled is true while the plan loads, so a refusal from the server locks the page as well.
  const isLocked =
    isNutritionist && (!planAllows || (templatesQuery.isError && isSubscriptionRequired(templatesQuery.error)));
  const isForbidden =
    !isNutritionist || (!isLocked && templatesQuery.isError && getErrorStatus(templatesQuery.error) === 403);

  const templates = templatesQuery.data?.templates ?? [];
  const totalCount = templatesQuery.data?.totalCount ?? 0;
  const hasActiveFilter =
    filters.search !== '' ||
    filters.goal !== null ||
    filters.weekCount !== null ||
    filters.mealsPerDay !== null ||
    filters.inUse !== null;
  const showEmptyState =
    !templatesQuery.isPending && !templatesQuery.isError && totalCount === 0 && !hasActiveFilter;

  if (isLocked) {
    return (
      <div className="flex h-full flex-col gap-4">
        <PageHeader eyebrow={t('library.eyebrow')} title={t('planTemplates.title')} />
        <PaywallPanel feature="planTemplates" />
      </div>
    );
  }

  if (isForbidden) {
    return (
      <div className="flex h-full flex-col gap-4">
        <PageHeader eyebrow={t('library.eyebrow')} title={t('planTemplates.title')} />
        <div className="flex flex-1 flex-col items-center justify-center gap-3 text-center">
          <Lock className="size-8 text-muted-foreground" aria-hidden="true" />
          <p className="text-copy font-bold text-ink">{t('planTemplates.nutritionistsOnly.title')}</p>
          <p className="max-w-search text-body text-muted-foreground">{t('planTemplates.nutritionistsOnly.body')}</p>
        </div>
      </div>
    );
  }

  const newTemplateButton = (
    <Button type="button" size="lg" className="gap-1.75 px-3.5 font-semibold" onClick={() => setSheetOpen(true)}>
      <Plus aria-hidden="true" />
      {t('planTemplates.newTemplate')}
    </Button>
  );

  return (
    <div className="flex h-full flex-col gap-4">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="flex flex-col gap-1.5">
          <PageHeader eyebrow={t('library.eyebrow')} title={t('planTemplates.title')} />
          <p className="text-copy text-muted-foreground">{t('planTemplates.subtitle')}</p>
        </div>
        {newTemplateButton}
      </div>

      <div className="flex flex-wrap items-center gap-3">
        <div className="relative min-w-40 max-w-search flex-1">
          <Search
            className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground"
            aria-hidden="true"
          />
          <Input
            type="search"
            value={searchInput}
            onChange={(event) => setSearchInput(event.target.value)}
            placeholder={t('planTemplates.searchPlaceholder')}
            className="h-9 pl-9"
            aria-label={t('planTemplates.searchPlaceholder')}
          />
        </div>
        {!showEmptyState && (
          <>
            <SingleOptionFilterPopover
              label={t('planTemplates.filters.goal')}
              options={GOAL_ORDER.map((value) => ({ value, label: t(`nutritionGoals.goal_${value}`) }))}
              selected={filters.goal}
              onChange={setGoal}
            />
            <SingleOptionFilterPopover
              label={t('planTemplates.filters.length')}
              options={WEEK_FILTER_VALUES.map((value) => ({
                value,
                label: t('planTemplates.table.weeks', { count: value }),
              }))}
              selected={filters.weekCount}
              onChange={setWeekCount}
            />
            <SingleOptionFilterPopover
              label={t('planTemplates.filters.mealsPerDay')}
              options={MEALS_FILTER_VALUES.map((value) => ({ value, label: String(value) }))}
              selected={filters.mealsPerDay}
              onChange={setMealsPerDay}
            />
            <SingleOptionFilterPopover<InUseValue>
              label={t('planTemplates.filters.inUse')}
              options={[
                { value: 'yes', label: t('planTemplates.filters.inUseYes') },
                { value: 'no', label: t('planTemplates.filters.inUseNo') },
              ]}
              selected={filters.inUse === null ? null : filters.inUse ? 'yes' : 'no'}
              onChange={(value) => setInUse(value === null ? null : value === 'yes')}
            />
          </>
        )}
      </div>

      {showEmptyState ? (
        <div className="flex flex-1 flex-col items-center justify-center gap-4 text-center">
          <div className="flex size-14 items-center justify-center rounded-full border border-border bg-card">
            <Plus className="size-6 text-muted-foreground" aria-hidden="true" />
          </div>
          <div className="flex flex-col gap-1">
            <p className="text-copy font-bold text-ink">{t('planTemplates.empty.title')}</p>
            <p className="text-body text-muted-foreground">{t('planTemplates.empty.hint')}</p>
          </div>
          <Button type="button" onClick={() => setSheetOpen(true)}>
            <Plus className="size-4" aria-hidden="true" />
            {t('planTemplates.empty.create')}
          </Button>
        </div>
      ) : (
        <div className="flex min-h-0 flex-1 flex-col overflow-hidden rounded-2xl border border-line bg-card shadow-panel">
          <div className="flex-1 overflow-y-auto [&_[data-slot=table-container]]:overflow-visible">
            <PlanTemplatesTable
              templates={templates}
              isPending={templatesQuery.isPending}
              isError={templatesQuery.isError}
              onRetry={() => void templatesQuery.refetch()}
              hasActiveFilter={hasActiveFilter}
              onClearFilters={clearFilters}
            />
          </div>
          <Pagination
            rowCount={templates.length}
            page={filters.page}
            pageSize={filters.pageSize}
            totalCount={totalCount}
            onPageChange={setPage}
          />
        </div>
      )}

      <NewPlanTemplateSheet open={sheetOpen} onOpenChange={setSheetOpen} />
    </div>
  );
}
