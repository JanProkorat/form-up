import { useTranslation } from 'react-i18next';
import type {
  AdminReportFunnelDto,
  AdminReportPeriod,
  AdminReportPlanChangeDto,
  AdminReportPlanChangesDto,
} from '@/api/generated';
import { ReportPanel, SectionHeader } from '@/components/admin-reports/ReportCard';
import { signedReportMoney, stepShare } from '@/components/admin-reports/reportFormat';

const FUNNEL_STEPS = ['signedUp', 'emailVerified', 'chosePlan', 'startedPaidPlan'] as const;

function FunnelPanel({ funnel, period }: { funnel: AdminReportFunnelDto; period: AdminReportPeriod }) {
  const { t, i18n } = useTranslation();
  const top = funnel.signedUp ?? 0;

  return (
    <ReportPanel
      title={t('adminReports.funnel.title')}
      subtitle={t('adminReports.funnel.subtitle', { window: t(`adminReports.period.window.${period}`) })}
    >
      <ol className="flex flex-col gap-2.5">
        {FUNNEL_STEPS.map((step, index) => {
          const value = funnel[step] ?? 0;
          const previous = index === 0 ? undefined : (funnel[FUNNEL_STEPS[index - 1]] ?? 0);
          const width = top > 0 ? Math.min(100, (value / top) * 100) : 0;
          return (
            <li key={step} className="flex items-center gap-3">
              <span className="w-28 shrink-0 text-body text-ink-2">{t(`adminReports.funnel.${step}`)}</span>
              <span className="flex h-6.5 grow overflow-hidden rounded-md bg-sunken">
                <span
                  className="flex min-w-8 items-center justify-end rounded-md bg-ink px-2 text-meta font-semibold text-background"
                  style={{ width: `${width}%` }}
                >
                  {value}
                </span>
              </span>
              <span className="w-16 text-right text-meta text-muted-foreground">
                {index === 0 ? '' : stepShare(value, previous, i18n.language)}
              </span>
            </li>
          );
        })}
      </ol>
      <span className="text-meta text-muted-foreground">{t('adminReports.funnel.note')}</span>
    </ReportPanel>
  );
}

interface ChangeRowProps {
  label: string;
  change: AdminReportPlanChangeDto | undefined;
  language: string;
}

function ChangeRow({ label, change, language }: ChangeRowProps) {
  const effects = (change?.revenueEffect ?? []).filter((effect) => (effect.amountMinorUnits ?? 0) !== 0);
  return (
    <div className="flex items-center gap-2.5 border-b border-line py-2.5 text-body">
      <span className="text-ink-2">{label}</span>
      <span className="ml-auto flex flex-col items-end font-semibold">
        {effects.length === 0 ? (
          <span className="font-normal text-muted-foreground">—</span>
        ) : (
          effects.map((effect) => {
            const amount = effect.amountMinorUnits ?? 0;
            return (
              <span key={effect.currency} className={amount > 0 ? 'text-success-ink' : 'text-error'}>
                {signedReportMoney(amount, effect.currency, language)}
              </span>
            );
          })
        )}
      </span>
      <span className="w-9 text-right font-semibold text-ink">{change?.count ?? 0}</span>
    </div>
  );
}

function PlanChangesPanel({ changes, period }: { changes: AdminReportPlanChangesDto; period: AdminReportPeriod }) {
  const { t, i18n } = useTranslation();
  const language = i18n.language;

  return (
    <ReportPanel
      title={t('adminReports.changes.title')}
      subtitle={t('adminReports.changes.subtitle', { window: t(`adminReports.period.window.${period}`) })}
    >
      <div className="flex flex-col">
        <ChangeRow label={t('adminReports.changes.upgraded')} change={changes.upgraded} language={language} />
        <ChangeRow label={t('adminReports.changes.downgraded')} change={changes.downgraded} language={language} />
        <ChangeRow label={t('adminReports.changes.cancelled')} change={changes.cancelled} language={language} />
        <div className="flex items-center gap-2.5 border-b border-line py-2.5 text-body">
          <span className="text-ink-2">{t('adminReports.changes.paymentFailed')}</span>
          <span className="ml-auto text-muted-foreground">{t('adminReports.changes.paymentFailedNote')}</span>
          <span className="w-9 text-right font-semibold text-ink">{changes.paymentFailedCoaches ?? 0}</span>
        </div>
        <div className="flex items-center gap-2.5 py-2.5 text-body">
          <span className="text-ink-2">{t('adminReports.changes.promotionCodes')}</span>
          <span className="ml-auto w-9 text-right font-semibold text-ink">{changes.promotionCodesApplied ?? 0}</span>
        </div>
      </div>
    </ReportPanel>
  );
}

interface Props {
  funnel: AdminReportFunnelDto;
  changes: AdminReportPlanChangesDto;
  period: AdminReportPeriod;
}

/** The Coaches section: the sign-up funnel and the plan changes of the period. */
export default function CoachesSection({ funnel, changes, period }: Props) {
  const { t } = useTranslation();
  return (
    <>
      <SectionHeader title={t('adminReports.coaches.title')} subtitle={t('adminReports.coaches.subtitle')} />
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1.65fr)_minmax(0,1fr)]">
        <FunnelPanel funnel={funnel} period={period} />
        <PlanChangesPanel changes={changes} period={period} />
      </div>
    </>
  );
}
