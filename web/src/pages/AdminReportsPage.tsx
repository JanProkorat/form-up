import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router-dom';
import { ExternalLink } from 'lucide-react';
import { AdminReportPeriod } from '@/api/generated';
import CoachesSection from '@/components/admin-reports/CoachesSection';
import MoneySection from '@/components/admin-reports/MoneySection';
import { stripeDashboardUrl } from '@/components/admin-reports/reportFormat';
import UsageSection from '@/components/admin-reports/UsageSection';
import { useAdminReports } from '@/components/admin-reports/useAdminReportsQuery';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';

const PERIODS: readonly AdminReportPeriod[] = [AdminReportPeriod.Days30, AdminReportPeriod.Days90, AdminReportPeriod.Months12];
const PERIOD_PARAM = 'period';

function periodFromParam(value: string | null): AdminReportPeriod {
  return PERIODS.find((period) => period === value) ?? AdminReportPeriod.Days30;
}

function LoadingBlocks() {
  return (
    <div className="flex flex-col gap-4" aria-hidden="true">
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
        {Array.from({ length: 4 }, (_, index) => (
          <Skeleton key={index} className="h-28 rounded-2xl" />
        ))}
      </div>
      <Skeleton className="h-64 rounded-2xl" />
      <Skeleton className="h-48 rounded-2xl" />
    </div>
  );
}

/** Form Up admin → Reports: totals over every coach (money, sign-up funnel, usage) for a chosen look-back window. */
export default function AdminReportsPage() {
  const { t } = useTranslation();
  const [searchParams, setSearchParams] = useSearchParams();
  const period = periodFromParam(searchParams.get(PERIOD_PARAM));
  const reportsQuery = useAdminReports(period);
  const stripeUrl = stripeDashboardUrl(import.meta.env.VITE_STRIPE_DASHBOARD_URL);
  const report = reportsQuery.data;

  const changePeriod = (next: AdminReportPeriod) => {
    setSearchParams(next === AdminReportPeriod.Days30 ? {} : { [PERIOD_PARAM]: next }, { replace: true });
  };

  return (
    <div className="flex flex-col gap-4.5 pb-10">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="flex flex-col gap-1.5">
          <h1 className="text-display font-semibold text-ink">{t('adminReports.title')}</h1>
          <p className="text-copy text-muted-foreground">{t('adminReports.subtitle')}</p>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <div
            role="group"
            aria-label={t('adminReports.period.label')}
            className="flex gap-0.5 rounded-xl border border-line bg-card p-0.75"
          >
            {PERIODS.map((option) => {
              const active = option === period;
              return (
                <button
                  key={option}
                  type="button"
                  aria-pressed={active}
                  onClick={() => changePeriod(option)}
                  className={`h-8 rounded-lg px-3 text-body font-semibold transition-colors ${active ? 'bg-primary text-primary-foreground' : 'text-ink-2 hover:text-ink'}`}
                >
                  {t(`adminReports.period.${option}`)}
                </button>
              );
            })}
          </div>
          {stripeUrl && (
            <a
              href={stripeUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex h-10 items-center gap-2 rounded-xl border border-line bg-card px-3.5 text-copy font-semibold text-ink hover:bg-sunken"
            >
              {t('adminReports.stripeDashboard')}
              <ExternalLink className="size-3.75" aria-hidden="true" />
            </a>
          )}
        </div>
      </div>

      {reportsQuery.isPending && <LoadingBlocks />}

      {reportsQuery.isError && (
        <div
          role="alert"
          className="flex flex-col items-center gap-3 rounded-2xl border border-line bg-card px-5 py-10 text-center shadow-panel"
        >
          <p className="text-body text-muted-foreground">{t('adminReports.loadError')}</p>
          <Button type="button" variant="outline" size="sm" onClick={() => void reportsQuery.refetch()}>
            {t('adminReports.retry')}
          </Button>
        </div>
      )}

      {!reportsQuery.isPending && !reportsQuery.isError && report && (
        <>
          <MoneySection report={report} period={period} />
          <CoachesSection funnel={report.funnel ?? {}} changes={report.planChanges ?? {}} period={period} />
          <UsageSection usage={report.usage} />
          <p className="text-meta text-muted-foreground">{t('adminReports.footnote')}</p>
        </>
      )}
    </div>
  );
}
