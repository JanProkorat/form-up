import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Plus, Search } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { QuestionnaireType } from '@/api/questionnaires';
import { useFormListParams } from '@/hooks/useFormListParams';
import { useTrainerQuestionnaires } from '@/hooks/useQuestionnaireQueries';
import { useDebouncedValue } from '@/hooks/useDebouncedValue';
import Pagination from '@/components/library/Pagination';
import SingleOptionFilterPopover from '@/components/plan-templates/SingleOptionFilterPopover';
import FormsTable from '@/components/forms/FormsTable';
import CreateFormDialog from '@/components/forms/CreateFormDialog';
import PaywallCard from '@/components/subscription/PaywallCard';
import { useSubscription } from '@/hooks/useSubscriptionQueries';

const TYPE_VALUES = [QuestionnaireType.Onboarding, QuestionnaireType.CheckIn] as const;

/**
 * The Forms list — search, a Type filter, the forms table, pagination and the
 * New form dialog. Open to Trainer and Nutritionist alike. The list endpoint
 * returns every form unpaged, so deleted (inactive) rows are dropped and
 * pagination happens here.
 */
export default function FormsPage() {
  const { t } = useTranslation();
  const { filters, setSearch, setType, setPage, clearFilters } = useFormListParams();
  const { isEntitled } = useSubscription();

  const [searchInput, setSearchInput] = useState(filters.search);
  const [createOpen, setCreateOpen] = useState(false);

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

  const formsQuery = useTrainerQuestionnaires({ type: filters.type ?? undefined, search: filters.search });

  // Delete is a soft delete: the server still returns the row with isActive=false.
  const activeForms = (formsQuery.data ?? []).filter((form) => form.isActive);
  const totalCount = activeForms.length;
  const lastPage = Math.max(1, Math.ceil(totalCount / filters.pageSize));
  const page = Math.min(filters.page, lastPage);
  const pageForms = activeForms.slice((page - 1) * filters.pageSize, page * filters.pageSize);

  // Unknown or failed plan lookups never lock the page; only a loaded plan without the feature does.
  const locked = !isEntitled('canSendQuestionnaires');

  const hasActiveFilter = filters.search !== '' || filters.type !== null;
  const showEmptyState = !formsQuery.isPending && !formsQuery.isError && totalCount === 0 && !hasActiveFilter;

  return (
    <div className="flex h-full flex-col gap-4">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="flex flex-col gap-1.5">
          <span className="flex items-center gap-1.75 text-label font-semibold tracking-label text-marker-text uppercase">
            <span className="size-1.75 rounded-full bg-marker" aria-hidden="true" />
            {t('forms.eyebrow')}
          </span>
          <h1 className="text-display font-semibold text-ink">{t('forms.title')}</h1>
          <p className="text-copy text-muted-foreground">{t('forms.subtitle')}</p>
        </div>
        {!locked && (
          <Button type="button" size="lg" className="gap-1.75 px-3.5 font-semibold" onClick={() => setCreateOpen(true)}>
            <Plus aria-hidden="true" />
            {t('forms.newForm')}
          </Button>
        )}
      </div>

      {locked ? (
        <div className="flex flex-1 items-start justify-center pt-10">
          <PaywallCard feature="forms" />
        </div>
      ) : (
      <>
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
            placeholder={t('forms.searchPlaceholder')}
            className="h-9 pl-9"
            aria-label={t('forms.searchPlaceholder')}
          />
        </div>
        {!showEmptyState && (
          <SingleOptionFilterPopover<QuestionnaireType>
            label={t('forms.filters.type')}
            options={TYPE_VALUES.map((value) => ({ value, label: t(`forms.type.${value}`) }))}
            selected={filters.type}
            onChange={setType}
          />
        )}
      </div>

      {showEmptyState ? (
        <div className="flex flex-1 flex-col items-center justify-center gap-4 text-center">
          <div className="flex size-14 items-center justify-center rounded-full border border-border bg-card">
            <Plus className="size-6 text-muted-foreground" aria-hidden="true" />
          </div>
          <div className="flex flex-col gap-1">
            <p className="text-copy font-bold text-ink">{t('forms.empty.title')}</p>
            <p className="text-body text-muted-foreground">{t('forms.empty.hint')}</p>
          </div>
          <Button type="button" onClick={() => setCreateOpen(true)}>
            <Plus className="size-4" aria-hidden="true" />
            {t('forms.empty.create')}
          </Button>
        </div>
      ) : (
        <div className="flex min-h-0 flex-col overflow-hidden rounded-2xl border border-line bg-card shadow-panel">
          <div className="min-h-0 overflow-y-auto [&_[data-slot=table-container]]:overflow-visible">
            <FormsTable
              forms={pageForms}
              isPending={formsQuery.isPending}
              isError={formsQuery.isError}
              onRetry={() => void formsQuery.refetch()}
              hasActiveFilter={hasActiveFilter}
              onClearFilters={clearFilters}
            />
          </div>
          <Pagination
            rowCount={pageForms.length}
            page={page}
            pageSize={filters.pageSize}
            totalCount={totalCount}
            onPageChange={setPage}
          />
        </div>
      )}
      </>
      )}

      <CreateFormDialog open={createOpen} onOpenChange={setCreateOpen} />
    </div>
  );
}
