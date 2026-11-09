import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import type { AdminCoachStatsDto } from '@/api/admin-coaches';
import { coachPlanName, formatCoachMoney } from '@/components/admin-coaches/coachFormat';
import { Skeleton } from '@/components/ui/skeleton';

interface TileProps {
  label: string;
  value: ReactNode;
  note: ReactNode;
}

function Tile({ label, value, note }: TileProps) {
  return (
    <div className="flex min-w-0 flex-col gap-1 rounded-2xl border border-line bg-card px-5 py-4 shadow-panel">
      <span className="text-body font-medium text-muted-foreground">{label}</span>
      <span className="text-display font-semibold text-ink">{value}</span>
      <span className="truncate text-meta text-muted-foreground">{note}</span>
    </div>
  );
}

interface Props {
  stats: AdminCoachStatsDto | undefined;
}

/** The four tiles over every coach: paying by plan, monthly revenue, payment failed, free and given free. */
export default function CoachStats({ stats }: Props) {
  const { t, i18n } = useTranslation();

  if (!stats) {
    return (
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
        {Array.from({ length: 4 }).map((_, index) => (
          <Skeleton key={index} className="h-28 rounded-2xl" />
        ))}
      </div>
    );
  }

  const byPlan = (stats.payingByPlan ?? [])
    .map((entry) => `${coachPlanName(entry.plan, i18n.language)} ${entry.count ?? 0}`)
    .join(' · ');
  const revenue = (stats.monthlyRevenue ?? []).filter((entry) => (entry.amountMinorUnits ?? 0) > 0);

  return (
    <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
      <Tile label={t('adminCoaches.stats.paying')} value={stats.payingCount ?? 0} note={byPlan || t('adminCoaches.stats.noPlans')} />
      <Tile
        label={t('adminCoaches.stats.revenue')}
        value={
          revenue.length === 0
            ? formatCoachMoney(0, 'CZK', i18n.language)
            : revenue.map((entry) => formatCoachMoney(entry.amountMinorUnits ?? 0, entry.currency, i18n.language)).join(' + ')
        }
        note={t('adminCoaches.stats.revenueNote')}
      />
      <Tile
        label={t('adminCoaches.stats.failed')}
        value={stats.paymentFailedCount ?? 0}
        note={t('adminCoaches.stats.failedNote')}
      />
      <Tile
        label={t('adminCoaches.stats.free')}
        value={`${stats.freeCount ?? 0} + ${stats.givenFreeCount ?? 0}`}
        note={t('adminCoaches.stats.freeNote')}
      />
    </div>
  );
}
