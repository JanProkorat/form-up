import { useTranslation } from 'react-i18next';
import LastDaysSection from '@/components/admin-overview/LastDaysSection';
import NeedsAttentionCard from '@/components/admin-overview/NeedsAttentionCard';
import QuickActionsCard from '@/components/admin-overview/QuickActionsCard';

/** Form Up admin → Overview: how the last 30 days went, what needs the admin now, and two shortcuts. */
export default function AdminOverviewPage() {
  const { t } = useTranslation();

  return (
    <div className="flex flex-col gap-5.5 pb-10">
      <div className="flex flex-col gap-1.5">
        <h1 className="text-display font-semibold text-ink">{t('adminOverview.title')}</h1>
        <p className="text-copy text-muted-foreground">{t('adminOverview.subtitle')}</p>
      </div>

      <LastDaysSection />

      <div className="grid grid-cols-1 items-start gap-4 xl:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
        <NeedsAttentionCard />
        <QuickActionsCard />
      </div>
    </div>
  );
}
