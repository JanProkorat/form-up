import { useTranslation } from 'react-i18next';
import { AdminReportPeriod, type AdminReportCurrencyAmountDto, type AdminReportMrrPointDto } from '@/api/generated';
import { ReportPanel } from '@/components/admin-reports/ReportCard';
import { formatAxisDay, formatAxisValue, niceAxisMax, reportMoney } from '@/components/admin-reports/reportFormat';

const WIDTH = 640;
const HEIGHT = 220;
const LEFT = 52;
const RIGHT = 630;
const TOP = 10;
const BOTTOM = 190;
const GRID_LINES = 3;
const MAX_X_LABELS = 6;

interface Point {
  day: string;
  full: number;
  after: number;
}

const amountOf = (amounts: readonly AdminReportCurrencyAmountDto[] | undefined, currency: string): number | undefined =>
  amounts?.find((amount) => amount.currency === currency)?.amountMinorUnits;

/** One point per snapshot day that has a value in this currency. */
function pointsFor(series: readonly AdminReportMrrPointDto[], currency: string): Point[] {
  const points: Point[] = [];
  for (const entry of series) {
    const full = amountOf(entry.full, currency);
    const after = amountOf(entry.afterPromotions, currency);
    if (entry.day && (full !== undefined || after !== undefined)) {
      points.push({ day: entry.day, full: full ?? after ?? 0, after: after ?? full ?? 0 });
    }
  }
  return points;
}

function currenciesOf(series: readonly AdminReportMrrPointDto[]): string[] {
  const found = new Set<string>();
  for (const entry of series) {
    for (const amount of [...(entry.full ?? []), ...(entry.afterPromotions ?? [])]) {
      if (amount.currency) {
        found.add(amount.currency);
      }
    }
  }
  return [...found];
}

interface ChartProps {
  currency: string;
  points: readonly Point[];
  monthOnly: boolean;
  showCurrency: boolean;
}

function Chart({ currency, points, monthOnly, showCurrency }: ChartProps) {
  const { t, i18n } = useTranslation();
  const language = i18n.language;

  const maxMajor = Math.max(...points.map((point) => point.full), ...points.map((point) => point.after)) / 100;
  const { step, top } = niceAxisMax(maxMajor);
  const x = (index: number) => (points.length === 1 ? RIGHT : LEFT + (index * (RIGHT - LEFT)) / (points.length - 1));
  const y = (minor: number) => BOTTOM - (Math.min(minor / 100, top) / top) * (BOTTOM - TOP);
  const polyline = (pick: (point: Point) => number) =>
    points.map((point, index) => `${x(index).toFixed(1)},${y(pick(point)).toFixed(1)}`).join(' ');

  const labelEvery = Math.ceil(points.length / MAX_X_LABELS);
  const last = points[points.length - 1];
  const first = points[0];
  const ariaLabel = t('adminReports.mrr.chartLabel', {
    from: reportMoney(first.after, currency, language),
    to: reportMoney(last.after, currency, language),
  });

  return (
    <div className="flex flex-col gap-1">
      {showCurrency && <span className="text-meta font-semibold text-ink-2">{currency}</span>}
      <svg
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        role="img"
        aria-label={ariaLabel}
        className="block h-auto w-full font-sans"
      >
        {Array.from({ length: GRID_LINES + 1 }, (_, index) => {
          const value = index * step;
          const gridY = BOTTOM - (index / GRID_LINES) * (BOTTOM - TOP);
          return (
            <g key={value}>
              <line x1={LEFT} y1={gridY} x2={RIGHT} y2={gridY} strokeWidth={1} className="stroke-line" />
              <text x={LEFT - 10} y={gridY + 4} textAnchor="end" className="fill-muted-foreground text-caption">
                {formatAxisValue(value, language)}
              </text>
            </g>
          );
        })}
        {points.map((point, index) =>
          index % labelEvery === 0 ? (
            <text key={point.day} x={x(index)} y={212} textAnchor="middle" className="fill-muted-foreground text-caption">
              {formatAxisDay(point.day, monthOnly, language)}
            </text>
          ) : null,
        )}
        {points.length > 1 && (
          <>
            <polyline
              points={polyline((point) => point.full)}
              fill="none"
              strokeWidth={2}
              strokeDasharray="5 4"
              strokeLinejoin="round"
              className="stroke-faint"
            />
            <polyline
              points={polyline((point) => point.after)}
              fill="none"
              strokeWidth={2.5}
              strokeLinejoin="round"
              strokeLinecap="round"
              className="stroke-marker"
            />
          </>
        )}
        <circle cx={x(points.length - 1)} cy={y(last.after)} r={4.5} strokeWidth={2} className="fill-marker stroke-card" />
      </svg>
    </div>
  );
}

const PERIOD_MONTH_ONLY: Record<AdminReportPeriod, boolean> = {
  [AdminReportPeriod.Days30]: false,
  [AdminReportPeriod.Days90]: false,
  [AdminReportPeriod.Months12]: true,
};

interface Props {
  series: readonly AdminReportMrrPointDto[];
  period: AdminReportPeriod;
}

/** Monthly recurring revenue per snapshot day: after promotions solid, full price dashed, one chart per currency. */
export default function MrrChart({ series, period }: Props) {
  const { t } = useTranslation();
  const currencies = currenciesOf(series);

  const legend = (
    <div className="flex gap-4 text-meta text-ink-2">
      <span className="flex items-center gap-1.5">
        <span className="h-0.75 w-4 rounded-xs bg-marker" aria-hidden="true" />
        {t('adminReports.mrr.afterPromotions')}
      </span>
      <span className="flex items-center gap-1.5">
        <span className="w-4 border-t-2 border-dashed border-faint" aria-hidden="true" />
        {t('adminReports.mrr.fullPrice')}
      </span>
    </div>
  );

  return (
    <ReportPanel title={t('adminReports.mrr.title')} subtitle={t('adminReports.mrr.subtitle')} aside={legend}>
      {currencies.length === 0 ? (
        <p className="py-8 text-center text-copy text-muted-foreground">{t('adminReports.mrr.empty')}</p>
      ) : (
        currencies.map((currency) => {
          const points = pointsFor(series, currency);
          return points.length === 0 ? null : (
            <Chart
              key={currency}
              currency={currency}
              points={points}
              monthOnly={PERIOD_MONTH_ONLY[period]}
              showCurrency={currencies.length > 1}
            />
          );
        })
      )}
    </ReportPanel>
  );
}
