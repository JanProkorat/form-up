import { useTranslation } from 'react-i18next';
import type { AdminReportUsageDto } from '@/api/generated';
import { ReportCard, SectionHeader, type ChipTone } from '@/components/admin-reports/ReportCard';
import { deltaPercent, formatNumber, formatPercentDelta } from '@/components/admin-reports/reportFormat';

interface Props {
  /** Null when no daily snapshot exists yet. */
  usage: AdminReportUsageDto | null | undefined;
}

/** The Usage section: five figures from the latest snapshot, or an honest empty state without one. */
export default function UsageSection({ usage }: Props) {
  const { t, i18n } = useTranslation();
  const language = i18n.language;

  if (!usage) {
    return (
      <>
        <SectionHeader title={t('adminReports.usage.title')} subtitle={t('adminReports.usage.subtitle')} />
        <p className="rounded-2xl border border-line bg-card px-5 py-6 text-copy text-muted-foreground shadow-panel">
          {t('adminReports.usage.empty')}
        </p>
      </>
    );
  }

  const clients = usage.activeClientsLast7Days;
  const clientsPercent = deltaPercent(clients?.value, clients?.previous);
  const clientsTone: ChipTone = clientsPercent === null || clientsPercent === 0 ? 'neutral' : clientsPercent > 0 ? 'good' : 'bad';

  return (
    <>
      <SectionHeader title={t('adminReports.usage.title')} subtitle={t('adminReports.usage.subtitle')} />
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-5">
        <ReportCard
          label={t('adminReports.usage.activeCoaches')}
          lines={[{ key: 'value', value: `${usage.activeCoachesLast30Days ?? 0} / ${usage.totalCoaches ?? 0}` }]}
          note={t('adminReports.usage.activeCoachesNote')}
        />
        <ReportCard
          label={t('adminReports.usage.clientsPerCoach')}
          lines={[{ key: 'value', value: formatNumber(usage.medianClientsPerCoach ?? 0, language) }]}
          note={t('adminReports.usage.clientsPerCoachNote')}
        />
        <ReportCard
          label={t('adminReports.usage.atCap')}
          lines={[{ key: 'value', value: formatNumber(usage.freeCoachesAtClientCap ?? 0, language) }]}
          note={t('adminReports.usage.atCapNote')}
        />
        <ReportCard
          label={t('adminReports.usage.activeClients')}
          lines={[
            {
              key: 'value',
              value: formatNumber(clients?.value ?? 0, language),
              chip: clientsPercent === null ? undefined : { text: formatPercentDelta(clientsPercent, language), tone: clientsTone },
            },
          ]}
          note={t('adminReports.usage.activeClientsNote')}
        />
        <ReportCard
          label={t('adminReports.usage.planWeeks')}
          lines={[{ key: 'value', value: formatNumber(usage.planWeeksPublishedLast7Days ?? 0, language) }]}
          note={t('adminReports.usage.planWeeksNote')}
        />
      </div>
    </>
  );
}
