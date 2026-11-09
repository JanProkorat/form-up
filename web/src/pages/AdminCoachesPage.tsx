import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Search } from 'lucide-react';
import { AdminCoachStatusFilter, COACHES_PAGE_SIZE } from '@/api/admin-coaches';
import { Input } from '@/components/ui/input';
import Pagination from '@/components/library/Pagination';
import CoachDrawer from '@/components/admin-coaches/CoachDrawer';
import CoachStats from '@/components/admin-coaches/CoachStats';
import CoachesTable from '@/components/admin-coaches/CoachesTable';
import { useAdminCoaches } from '@/components/admin-coaches/useAdminCoachesQueries';
import { cn } from '@/lib/utils';

const SEARCH_DEBOUNCE_MS = 300;

const FILTERS: readonly AdminCoachStatusFilter[] = [
  AdminCoachStatusFilter.All,
  AdminCoachStatusFilter.Paying,
  AdminCoachStatusFilter.PaymentFailed,
  AdminCoachStatusFilter.Cancelling,
  AdminCoachStatusFilter.Free,
  AdminCoachStatusFilter.GivenFree,
];

/** Form Up admin → Coaches: stats, filters and a table of every coach account, with a drawer per coach. */
export default function AdminCoachesPage() {
  const { t } = useTranslation();
  const [status, setStatus] = useState<AdminCoachStatusFilter>(AdminCoachStatusFilter.All);
  const [searchText, setSearchText] = useState('');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const [openCoachId, setOpenCoachId] = useState<string | null>(null);

  useEffect(() => {
    const handle = window.setTimeout(() => {
      setSearch(searchText.trim());
      setPage(1);
    }, SEARCH_DEBOUNCE_MS);
    return () => window.clearTimeout(handle);
  }, [searchText]);

  const coachesQuery = useAdminCoaches({ page, status, search });
  const stats = coachesQuery.data?.stats;
  const coaches = coachesQuery.data?.items ?? [];

  const countOf = (filter: AdminCoachStatusFilter): number | undefined => {
    switch (filter) {
      case AdminCoachStatusFilter.All:
        return stats?.allCount;
      case AdminCoachStatusFilter.Paying:
        return stats?.payingCount;
      case AdminCoachStatusFilter.PaymentFailed:
        return stats?.paymentFailedCount;
      case AdminCoachStatusFilter.Cancelling:
        return stats?.cancellingCount;
      case AdminCoachStatusFilter.Free:
        return stats?.freeCount;
      case AdminCoachStatusFilter.GivenFree:
        return stats?.givenFreeCount;
    }
  };

  return (
    <div className="flex h-full flex-col gap-4">
      <div className="flex flex-col gap-1.5">
        <h1 className="text-display font-semibold text-ink">{t('adminCoaches.title')}</h1>
        <p className="text-copy text-muted-foreground">{t('adminCoaches.subtitle')}</p>
      </div>

      <CoachStats stats={stats} />

      <div className="flex flex-wrap items-center justify-between gap-3">
        <div role="group" aria-label={t('adminCoaches.filters.label')} className="flex flex-wrap gap-2">
          {FILTERS.map((filter) => {
            const selected = status === filter;
            const count = countOf(filter);
            return (
              <button
                key={filter}
                type="button"
                aria-pressed={selected}
                onClick={() => {
                  setStatus(filter);
                  setPage(1);
                }}
                className={cn(
                  'inline-flex items-center gap-1.5 rounded-full px-3.5 py-1.5 text-body font-medium outline-none focus-visible:ring-3 focus-visible:ring-ring/50',
                  selected ? 'bg-ink text-primary-foreground' : 'bg-sunken text-ink-2 hover:bg-muted',
                )}
              >
                {t(`adminCoaches.filters.${filter}`)}
                {count !== undefined && <span className="text-meta opacity-70">{count}</span>}
              </button>
            );
          })}
        </div>
        <div className="relative w-full sm:w-72">
          <Search
            className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground"
            aria-hidden="true"
          />
          <Input
            type="search"
            value={searchText}
            onChange={(event) => setSearchText(event.target.value)}
            placeholder={t('adminCoaches.search')}
            aria-label={t('adminCoaches.search')}
            className="h-10 pl-9"
          />
        </div>
      </div>

      <div className="flex min-h-0 flex-col overflow-hidden rounded-2xl border border-line bg-card shadow-panel">
        <div className="min-h-0 overflow-y-auto [&_[data-slot=table-container]]:overflow-visible">
          <CoachesTable
            coaches={coaches}
            isPending={coachesQuery.isPending}
            isError={coachesQuery.isError}
            onRetry={() => void coachesQuery.refetch()}
            onOpen={setOpenCoachId}
          />
        </div>
        <Pagination
          rowCount={coaches.length}
          page={page}
          pageSize={COACHES_PAGE_SIZE}
          totalCount={coachesQuery.data?.totalCount ?? 0}
          onPageChange={setPage}
        />
      </div>

      <CoachDrawer coachId={openCoachId} onClose={() => setOpenCoachId(null)} />
    </div>
  );
}
