import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router-dom';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import { Button } from '@/components/ui/button';
import CheckInsTable from '@/components/check-ins/CheckInsTable';
import {
  countCheckIns,
  mondayOf,
  shiftWeek,
  sortCheckIns,
  toCheckInState,
  toDateKey,
  weekNumberOf,
  weekRangeLabel,
  type CheckInFilter,
} from '@/components/check-ins/checkInsModel';
import PaywallCard from '@/components/subscription/PaywallCard';
import { useMarkCheckInReviewed, useTrainerWeeklyCheckIns } from '@/hooks/useWeeklyCheckIns';
import { useSubscription } from '@/hooks/useSubscriptionQueries';
import { showApiError } from '@/lib/api-errors';
import { cn } from '@/lib/utils';

const FILTERS: readonly CheckInFilter[] = ['all', 'notReviewed', 'missed', 'waiting', 'reviewed'];

/** The Monday of the week the `week` param points at; a missing or malformed value means the current week. */
function parseWeekParam(value: string | null): Date {
  const match = value ? /^(\d{4})-(\d{2})-(\d{2})$/.exec(value) : null;
  if (match) {
    const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
    const date = new Date(year, month - 1, day, 12, 0, 0, 0);
    if (date.getFullYear() === year && date.getMonth() === month - 1 && date.getDate() === day) {
      return mondayOf(date);
    }
  }
  return mondayOf(new Date());
}

function parseFilterParam(value: string | null): CheckInFilter {
  return FILTERS.find((candidate) => candidate === value) ?? 'all';
}

function TotalCard({ label, value, subline, accent = false }: { label: string; value: string; subline: string; accent?: boolean }) {
  return (
    <div className="flex min-w-0 flex-col gap-1 rounded-2xl border border-line bg-card px-4.5 py-4">
      <span className="text-meta font-semibold text-muted-foreground">{label}</span>
      <span className={cn('font-display text-stat font-semibold', accent ? 'text-error' : 'text-ink')}>{value}</span>
      <span className="text-meta text-muted-foreground">{subline}</span>
    </div>
  );
}

/** Forms → Check-ins: one week of weekly check-ins across all clients, with review actions. */
export default function CheckInsPage() {
  const { t, i18n } = useTranslation();
  const { isEntitled } = useSubscription();
  const locked = !isEntitled('canUseWeeklyCheckIns');

  // Week and status live in the URL so Back from "Open answers" returns to the same view.
  const [searchParams, setSearchParams] = useSearchParams();
  const weekParam = searchParams.get('week');
  const monday = useMemo(() => parseWeekParam(weekParam), [weekParam]);
  const filter = parseFilterParam(searchParams.get('status'));
  const [markingId, setMarkingId] = useState<string | null>(null);

  const updateView = (nextMonday: Date, nextFilter: CheckInFilter) => {
    const next = new URLSearchParams(searchParams);
    next.delete('week');
    next.delete('status');
    if (toDateKey(nextMonday) !== toDateKey(mondayOf(new Date()))) {
      next.set('week', toDateKey(nextMonday));
    }
    if (nextFilter !== 'all') {
      next.set('status', nextFilter);
    }
    setSearchParams(next, { replace: true });
  };

  const weekStartDate = toDateKey(monday);
  const query = useTrainerWeeklyCheckIns(weekStartDate);
  const markReviewed = useMarkCheckInReviewed();

  const rows = useMemo(() => sortCheckIns(query.data ?? []), [query.data]);
  const counts = useMemo(() => countCheckIns(rows), [rows]);
  const visibleRows = filter === 'all' ? rows : rows.filter((row) => toCheckInState(row.status) === filter);

  const answered = counts.notReviewed + counts.reviewed;
  const percent = counts.all === 0 ? 0 : Math.round((answered / counts.all) * 100);
  const loaded = !query.isPending && !query.isError;

  const handleMarkReviewed = (id: string) => {
    setMarkingId(id);
    markReviewed.mutate(id, {
      onError: (error) => showApiError(error, 'checkIns.markReviewedError'),
      onSettled: () => setMarkingId(null),
    });
  };

  const weekLabel = t('checkIns.week.label', {
    week: weekNumberOf(monday),
    range: weekRangeLabel(monday, i18n.language),
  });

  return (
    <div className="flex h-full min-w-0 flex-col gap-4">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="flex flex-col gap-1.5">
          <h1 className="text-display font-semibold text-ink">{t('checkIns.title')}</h1>
          <p className="text-copy text-muted-foreground">{t('checkIns.subtitle')}</p>
        </div>
        {!locked && (
          <div role="group" aria-label={t('checkIns.week.group')} className="flex items-center gap-2">
            <Button
              type="button"
              variant="outline"
              size="icon-lg"
              aria-label={t('checkIns.week.previous')}
              onClick={() => updateView(shiftWeek(monday, -1), filter)}
            >
              <ChevronLeft aria-hidden="true" />
            </Button>
            <span className="min-w-42 text-center text-body font-semibold text-ink">{weekLabel}</span>
            <Button
              type="button"
              variant="outline"
              size="icon-lg"
              aria-label={t('checkIns.week.next')}
              onClick={() => updateView(shiftWeek(monday, 1), filter)}
            >
              <ChevronRight aria-hidden="true" />
            </Button>
          </div>
        )}
      </div>

      {locked ? (
        <div className="flex flex-1 items-start justify-center pt-10">
          <PaywallCard feature="checkIns" />
        </div>
      ) : (
        <>
          <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
            <TotalCard
              label={t('checkIns.totals.answered')}
              value={t('checkIns.totals.answeredValue', { answered, total: counts.all })}
              subline={t('checkIns.totals.answeredSubline', { percent })}
            />
            <TotalCard
              label={t('checkIns.status.notReviewed')}
              value={String(counts.notReviewed)}
              subline={t('checkIns.totals.notReviewedSubline')}
              accent={counts.notReviewed > 0}
            />
            <TotalCard
              label={t('checkIns.status.missed')}
              value={String(counts.missed)}
              subline={t('checkIns.totals.missedSubline')}
            />
            <TotalCard
              label={t('checkIns.status.waiting')}
              value={String(counts.waiting)}
              subline={t('checkIns.totals.waitingSubline')}
            />
          </div>

          <div className="flex flex-wrap items-center justify-between gap-3">
            <div role="group" aria-label={t('checkIns.filters.label')} className="flex flex-wrap gap-2">
              {FILTERS.map((value) => (
                <button
                  key={value}
                  type="button"
                  aria-pressed={filter === value}
                  onClick={() => updateView(monday, value)}
                  className={cn(
                    'h-8 rounded-full border px-3 text-body font-medium outline-none transition-colors focus-visible:ring-3 focus-visible:ring-ring/50',
                    filter === value
                      ? 'border-primary bg-primary text-primary-foreground'
                      : 'border-line bg-card text-ink-2 hover:bg-sunken',
                  )}
                >
                  {t(value === 'all' ? 'checkIns.filters.all' : `checkIns.status.${value}`)}
                  {loaded && ` ${counts[value]}`}
                </button>
              ))}
            </div>
            <span className="text-meta text-muted-foreground">{t('checkIns.timeZoneNote')}</span>
          </div>

          <div className="flex min-h-0 min-w-0 flex-col overflow-hidden rounded-2xl border border-line bg-card shadow-panel">
            {/* One scroll container for both axes keeps the sticky header working and the card's border rounded. */}
            <div className="min-h-0 min-w-0 overflow-auto [&_[data-slot=table-container]]:overflow-visible">
              <CheckInsTable
                rows={visibleRows}
                emptyText={t(rows.length === 0 ? 'checkIns.empty' : 'checkIns.emptyFilter')}
                isPending={query.isPending}
                isError={query.isError}
                onRetry={() => void query.refetch()}
                markingId={markingId}
                onMarkReviewed={handleMarkReviewed}
              />
            </div>
          </div>
          {loaded && (
            <p className="text-meta text-muted-foreground">
              {t('checkIns.footer', { shown: visibleRows.length, total: counts.all })}
            </p>
          )}
        </>
      )}
    </div>
  );
}
