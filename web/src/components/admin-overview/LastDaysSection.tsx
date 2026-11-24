import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { AdminReportPeriod } from '@/api/generated';
import { ReportCard } from '@/components/admin-reports/ReportCard';
import { useAdminReports } from '@/components/admin-reports/useAdminReportsQuery';
import { buildOverviewTiles, planBreakdown } from '@/components/admin-overview/overviewFormat';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';

const REPORTS_PATH = '/admin/business-reports';

/** The four headline figures for the last 30 days, each against the 30 days before. Fails and retries on its own. */
export default function LastDaysSection() {
  const { t, i18n } = useTranslation();
  const language = i18n.language;
  const reportsQuery = useAdminReports(AdminReportPeriod.Days30);
  const report = reportsQuery.data;

  const tiles = report ? buildOverviewTiles(report, t('adminOverview.lastDays.pointsUnit'), language) : [];
  const breakdown = report ? planBreakdown(report, language) : '';
  const notes: Record<string, string> = {
    mrr: t('adminOverview.lastDays.mrrNote'),
    signups: t('adminOverview.lastDays.signupsNote'),
    payingCoaches: breakdown || t('adminOverview.lastDays.payingCoachesNote'),
    churn: t('adminOverview.lastDays.churnNote'),
  };

  return (
    <section aria-labelledby="overview-last-days" className="flex flex-col gap-3">
      <div className="flex items-baseline gap-3">
        <h2 id="overview-last-days" className="font-display text-card-title font-semibold text-ink">
          {t('adminOverview.lastDays.title')}
        </h2>
        <Link to={REPORTS_PATH} className="ml-auto text-body font-semibold text-ink hover:text-ink-2">
          {t('adminOverview.lastDays.fullReports')}
        </Link>
      </div>

      {reportsQuery.isPending && (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4" aria-hidden="true">
          {Array.from({ length: 4 }, (_, index) => (
            <Skeleton key={index} className="h-28 rounded-2xl" />
          ))}
        </div>
      )}

      {reportsQuery.isError && (
        <div
          role="alert"
          className="flex flex-col items-center gap-3 rounded-2xl border border-line bg-card px-5 py-8 text-center shadow-panel"
        >
          <p className="text-body text-muted-foreground">{t('adminOverview.lastDays.loadError')}</p>
          <Button type="button" variant="outline" size="sm" onClick={() => void reportsQuery.refetch()}>
            {t('adminOverview.retry')}
          </Button>
        </div>
      )}

      {!reportsQuery.isPending && !reportsQuery.isError && report && (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
          {tiles.map((tile) => (
            <ReportCard
              key={tile.id}
              label={t(`adminOverview.lastDays.${tile.id}`)}
              lines={[{ key: tile.id, value: tile.value, chip: tile.chip }]}
              note={notes[tile.id]}
            />
          ))}
        </div>
      )}
    </section>
  );
}
