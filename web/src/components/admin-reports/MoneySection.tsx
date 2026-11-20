import { useTranslation } from 'react-i18next';
import {
  AdminReportPeriod,
  type AdminReportCurrencyValueDto,
  type AdminReportMoneyDto,
  type GetAdminReportsResponse,
} from '@/api/generated';
import MrrChart from '@/components/admin-reports/MrrChart';
import PlansPanel from '@/components/admin-reports/PlansPanel';
import { ReportCard, SectionHeader, type ChipTone, type FigureLine } from '@/components/admin-reports/ReportCard';
import {
  deltaPercent,
  formatCountDelta,
  formatNumber,
  formatPercentDelta,
  formatReportDay,
  reportMoney,
} from '@/components/admin-reports/reportFormat';

const NO_VALUE: FigureLine = { key: 'none', value: '—' };

function percentTone(percent: number): ChipTone {
  if (percent > 0) {
    return 'good';
  }
  return percent < 0 ? 'bad' : 'neutral';
}

/** One value line per currency, each with its own percent change; currencies are never added together. */
function moneyLines(items: readonly AdminReportCurrencyValueDto[] | undefined, language: string): FigureLine[] {
  if (!items || items.length === 0) {
    return [NO_VALUE];
  }
  return items.map((item) => {
    const percent = deltaPercent(item.value, item.previous);
    return {
      key: item.currency ?? 'none',
      value: reportMoney(item.value ?? 0, item.currency, language),
      chip: percent === null ? undefined : { text: formatPercentDelta(percent, language), tone: percentTone(percent) },
    };
  });
}

function lostLines(money: AdminReportMoneyDto, language: string): FigureLine[] {
  const lost = money.lostToCancellations;
  const amounts = lost?.amounts ?? [];
  const count = lost?.count;
  const countDelta = count?.previous === undefined || count.previous === null ? null : (count.value ?? 0) - count.previous;
  // More cancellations than the baseline is bad news, fewer is good.
  const chip =
    countDelta === null
      ? undefined
      : { text: formatCountDelta(countDelta), tone: (countDelta > 0 ? 'bad' : countDelta < 0 ? 'good' : 'neutral') as ChipTone };
  const lines: FigureLine[] =
    amounts.length === 0
      ? [NO_VALUE]
      : amounts.map((amount) => ({
          key: amount.currency ?? 'none',
          value: reportMoney(amount.value ?? 0, amount.currency, language),
          danger: true,
        }));
  return lines.map((line, index) => (index === 0 ? { ...line, chip } : line));
}

function averageLines(money: AdminReportMoneyDto, language: string): FigureLine[] {
  const averages = money.averagePerPayingCoach ?? [];
  if (averages.length === 0) {
    return [NO_VALUE];
  }
  return averages.map((item) => {
    const percent = deltaPercent(item.value, item.previous);
    return {
      key: item.currency ?? 'none',
      value: reportMoney(item.value ?? 0, item.currency, language),
      chip: percent === null ? undefined : { text: formatPercentDelta(percent, language), tone: percentTone(percent) },
    };
  });
}

const PERIOD_AGO: Record<AdminReportPeriod, string> = {
  [AdminReportPeriod.Days30]: 'adminReports.period.ago.Days30',
  [AdminReportPeriod.Days90]: 'adminReports.period.ago.Days90',
  [AdminReportPeriod.Months12]: 'adminReports.period.ago.Months12',
};

interface Props {
  report: GetAdminReportsResponse;
  period: AdminReportPeriod;
}

/** The Money section: four figure cards, the MRR chart and the paying-coaches-by-plan panel. */
export default function MoneySection({ report, period }: Props) {
  const { t, i18n } = useTranslation();
  const language = i18n.language;
  const money = report.money ?? {};

  const snapshot = formatReportDay(report.snapshotDay, language);
  const compared = formatReportDay(report.comparedFrom, language);
  // A named baseline date is more honest than "30 days ago" when the baseline snapshot sits elsewhere.
  const against = compared ? t('adminReports.money.againstDate', { date: compared }) : t(PERIOD_AGO[period]);
  const subtitle = snapshot ? t('adminReports.money.subtitle', { day: snapshot, against }) : t('adminReports.money.noSnapshot');

  const fullPrice = (money.monthlyRevenueFull ?? [])
    .map((item) => reportMoney(item.value ?? 0, item.currency, language))
    .join(' · ');
  const billedCoaches = (money.averagePerPayingCoach ?? []).reduce((sum, item) => sum + (item.coachesBilled ?? 0), 0);
  const cancelled = money.lostToCancellations?.count?.value ?? 0;
  const churnNote =
    money.churnPercent === undefined || money.churnPercent === null
      ? t('adminReports.money.lostNote', { count: cancelled })
      : t('adminReports.money.lostNoteChurn', { count: cancelled, churn: formatNumber(money.churnPercent, language) });

  return (
    <>
      <SectionHeader title={t('adminReports.money.title')} subtitle={subtitle} />
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <ReportCard
          label={t('adminReports.money.revenue')}
          lines={moneyLines(money.monthlyRevenueAfterPromotions, language)}
          note={fullPrice ? t('adminReports.money.revenueNote', { full: fullPrice }) : t('adminReports.money.revenueNoteShort')}
        />
        <ReportCard
          label={t('adminReports.money.collected')}
          lines={moneyLines(money.collectedThisMonth, language)}
          note={t('adminReports.money.collectedNote')}
        />
        <ReportCard label={t('adminReports.money.lost')} lines={lostLines(money, language)} note={churnNote} />
        <ReportCard
          label={t('adminReports.money.average')}
          lines={averageLines(money, language)}
          note={t('adminReports.money.averageNote', { count: billedCoaches })}
        />
      </div>
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1.65fr)_minmax(0,1fr)]">
        <MrrChart series={report.mrrSeries ?? []} period={period} />
        <PlansPanel plans={money.plans ?? []} addOns={money.addOns ?? []} />
      </div>
    </>
  );
}
