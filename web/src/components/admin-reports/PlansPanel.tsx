import { useTranslation } from 'react-i18next';
import type { AdminReportAddOnDto, AdminReportPlanRevenueDto } from '@/api/generated';
import { ReportPanel } from '@/components/admin-reports/ReportCard';
import { formatNumber, reportMoney, reportPlanName } from '@/components/admin-reports/reportFormat';

/** Segment colours, lightest to darkest like the board; further plans reuse the last ones. */
const PLAN_COLORS = ['bg-mark-ring', 'bg-mark-row2', 'bg-mark-row1', 'bg-ink-2', 'bg-faint'] as const;

interface RowProps {
  swatch: string;
  name: string;
  revenue: readonly string[];
  count: number;
}

function Row({ swatch, name, revenue, count }: RowProps) {
  const { t } = useTranslation();
  return (
    <div className="flex items-center gap-2.5 border-b border-line py-2.5 text-body last:border-b-0">
      <span className={`size-2.5 shrink-0 rounded-xs ${swatch}`} aria-hidden="true" />
      <span className="min-w-0 truncate font-semibold text-ink">{name}</span>
      <span className="ml-auto flex flex-col items-end text-muted-foreground">
        {revenue.length === 0 ? (
          <span>—</span>
        ) : (
          revenue.map((line) => (
            <span key={line} className="whitespace-nowrap">
              {t('adminReports.plans.perMonth', { amount: line })}
            </span>
          ))
        )}
      </span>
      <span className="w-11 text-right font-semibold text-ink">{count}</span>
    </div>
  );
}

interface Props {
  plans: readonly AdminReportPlanRevenueDto[];
  addOns: readonly AdminReportAddOnDto[];
}

/** Paying coaches per plan as a stacked bar and rows, plus the second-role add-on row. */
export default function PlansPanel({ plans, addOns }: Props) {
  const { t, i18n } = useTranslation();
  const language = i18n.language;
  const total = plans.reduce((sum, plan) => sum + (plan.payingCoaches ?? 0), 0);
  const addOnCount = addOns.reduce((sum, addOn) => sum + (addOn.count ?? 0), 0);
  const addOnRevenue = addOns
    .filter((addOn) => (addOn.revenueMinorUnits ?? 0) > 0)
    .map((addOn) => reportMoney(addOn.revenueMinorUnits ?? 0, addOn.currency, language));
  const colorOf = (index: number) => PLAN_COLORS[Math.min(index, PLAN_COLORS.length - 1)];
  const summary = plans.map((plan) => `${reportPlanName(plan, language)} ${plan.payingCoaches ?? 0}`).join(', ');

  return (
    <ReportPanel
      title={t('adminReports.plans.title')}
      subtitle={t('adminReports.plans.subtitle', { count: formatNumber(total, language) })}
    >
      {total > 0 && (
        <div
          role="img"
          aria-label={t('adminReports.plans.barLabel', { summary })}
          className="flex h-3.5 gap-0.5 overflow-hidden rounded-full"
        >
          {plans.map((plan, index) =>
            (plan.payingCoaches ?? 0) > 0 ? (
              <span
                key={plan.planCode ?? index}
                className={colorOf(index)}
                style={{ width: `${((plan.payingCoaches ?? 0) / total) * 100}%` }}
              />
            ) : null,
          )}
        </div>
      )}
      <div className="flex flex-col">
        {plans.map((plan, index) => (
          <Row
            key={plan.planCode ?? index}
            swatch={colorOf(index)}
            name={reportPlanName(plan, language)}
            revenue={(plan.revenue ?? []).map((amount) => reportMoney(amount.amountMinorUnits ?? 0, amount.currency, language))}
            count={plan.payingCoaches ?? 0}
          />
        ))}
        <Row
          swatch="border-[1.5px] border-muted-foreground"
          name={t('adminReports.plans.addOn')}
          revenue={addOnRevenue}
          count={addOnCount}
        />
      </div>
    </ReportPanel>
  );
}
