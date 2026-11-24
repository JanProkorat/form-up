import { PaymentCurrency, type AdminReportCurrencyValueDto, type GetAdminReportsResponse } from '@/api/generated';
import type { ChipTone } from '@/components/admin-reports/ReportCard';
import {
  deltaPercent,
  formatNumber,
  formatPercentDelta,
  reportMoney,
  reportPlanName,
} from '@/components/admin-reports/reportFormat';

const MINUS = '−';
const DASH = '—';

export interface TileChip {
  text: string;
  tone: ChipTone;
}

/** One figure tile of the Last 30 days section; `note` is filled by the caller where it needs translated copy. */
export interface OverviewTile {
  id: 'mrr' | 'signups' | 'payingCoaches' | 'churn';
  value: string;
  chip?: TileChip;
}

function toneOf(change: number, upIsBad: boolean): ChipTone {
  if (change === 0) {
    return 'neutral';
  }
  return change > 0 === upIsBad ? 'bad' : 'good';
}

/** A relative change chip; none when there is no usable baseline (missing, null or 0). */
export function percentChip(
  value: number | undefined,
  previous: number | undefined,
  language: string,
): TileChip | undefined {
  const percent = deltaPercent(value, previous);
  return percent === null ? undefined : { text: formatPercentDelta(percent, language), tone: toneOf(percent, false) };
}

/** Churn change in percentage points, one decimal; up is bad. None unless both figures are numbers. */
export function churnChip(
  churn: number | null | undefined,
  previous: number | null | undefined,
  unit: string,
  language: string,
): TileChip | undefined {
  if (typeof churn !== 'number' || typeof previous !== 'number') {
    return undefined;
  }
  const points = Math.round((churn - previous) * 10) / 10;
  const formatted = new Intl.NumberFormat(language, {
    minimumFractionDigits: 1,
    maximumFractionDigits: 1,
    signDisplay: 'exceptZero',
  }).format(points);
  return { text: `${formatted.replace('-', MINUS)} ${unit}`, tone: toneOf(points, true) };
}

/** The CZK entry when there is one, otherwise the first; currencies are never added together. */
function pickMrr(items: readonly AdminReportCurrencyValueDto[] | undefined): AdminReportCurrencyValueDto | undefined {
  return items?.find((item) => item.currency === PaymentCurrency.CZK) ?? items?.[0];
}

/** "Start 18 · Pro 13": paying coaches per plan in API order, plans without any left out. */
export function planBreakdown(report: GetAdminReportsResponse, language: string): string {
  return (report.money?.plans ?? [])
    .filter((plan) => (plan.payingCoaches ?? 0) > 0)
    .map((plan) => `${reportPlanName(plan, language)} ${plan.payingCoaches}`)
    .join(' · ');
}

/** The four Last 30 days tiles with their change badges against the previous 30 days. */
export function buildOverviewTiles(report: GetAdminReportsResponse, unit: string, language: string): OverviewTile[] {
  const money = report.money;
  const mrr = pickMrr(money?.monthlyRevenueAfterPromotions);
  const paying = money?.payingCoaches;
  const churn = money?.churnPercent;

  return [
    {
      id: 'mrr',
      value: mrr ? reportMoney(mrr.value ?? 0, mrr.currency, language) : DASH,
      chip: mrr ? percentChip(mrr.value ?? 0, mrr.previous, language) : undefined,
    },
    {
      id: 'signups',
      value: String(report.funnel?.signedUp ?? 0),
      chip: percentChip(report.funnel?.signedUp ?? 0, report.funnel?.signedUpPrevious, language),
    },
    {
      id: 'payingCoaches',
      value: String(paying?.value ?? 0),
      chip: percentChip(paying?.value ?? 0, paying?.previous, language),
    },
    {
      id: 'churn',
      value: typeof churn === 'number' ? `${formatNumber(churn, language)} %` : DASH,
      chip: churnChip(churn, money?.churnPercentPrevious, unit, language),
    },
  ];
}
