import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Info, Plus } from 'lucide-react';
import { Button } from '@/components/ui/button';
import type { SubscriptionPlanDto } from '@/api/admin-subscription-plans';
import { showApiError, showSuccess } from '@/lib/api-errors';
import PlansTable from '@/components/admin-plans/PlansTable';
import { useAdminPlans, useStopOfferingPlan } from '@/components/admin-plans/useAdminPlansQueries';

/** Form Up admin → Plans: the plan catalogue; a plan opens in the plan editor page. */
export default function AdminPlansPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const plansQuery = useAdminPlans();
  const stopOffering = useStopOfferingPlan();

  const plans = plansQuery.data ?? [];

  const handleEdit = (plan: SubscriptionPlanDto) => {
    if (plan.code) {
      navigate(`/admin/plans/${encodeURIComponent(plan.code)}`);
    }
  };

  const handleStopOffering = (plan: SubscriptionPlanDto) => {
    const code = plan.code;
    if (!code) {
      return;
    }
    stopOffering.mutate(code, {
      onSuccess: () => showSuccess('adminPlans.stopSuccess'),
      onError: (error) => showApiError(error, 'adminPlans.stopError'),
    });
  };

  return (
    <div className="flex h-full flex-col gap-4">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="flex flex-col gap-1.5">
          <h1 className="text-display font-semibold text-ink">{t('adminPlans.title')}</h1>
          <p className="text-copy text-muted-foreground">{t('adminPlans.subtitle')}</p>
        </div>
        <Button type="button" size="lg" className="gap-1.75 px-3.5 font-semibold" onClick={() => navigate('/admin/plans/new')}>
          <Plus aria-hidden="true" />
          {t('adminPlans.newPlan')}
        </Button>
      </div>

      <div className="flex min-h-0 flex-col overflow-hidden rounded-2xl border border-line bg-card shadow-panel">
        <div className="min-h-0 overflow-y-auto [&_[data-slot=table-container]]:overflow-visible">
          <PlansTable
            plans={plans}
            isPending={plansQuery.isPending}
            isError={plansQuery.isError}
            onRetry={() => void plansQuery.refetch()}
            onEdit={handleEdit}
            onStopOffering={handleStopOffering}
          />
        </div>
      </div>

      <p className="flex items-center gap-2 text-body text-muted-foreground">
        <Info className="size-4 shrink-0" aria-hidden="true" />
        {t('adminPlans.note')}
      </p>
    </div>
  );
}
