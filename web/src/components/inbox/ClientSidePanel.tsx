import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/api/client';
import { getClientPlans } from '@/api/client-plans';
import { getClientMeasurements } from '@/api/measurements';
import { useMeasurementsGate } from '@/hooks/useMeasurementsGate';
import { getErrorStatus } from '@/lib/api-errors';
import { Skeleton } from '@/components/ui/skeleton';
import NotAClientPanel from '@/components/inbox/panel/NotAClientPanel';
import PanelIdentity from '@/components/inbox/panel/PanelIdentity';
import PanelStats from '@/components/inbox/panel/PanelStats';
import MealPlanPanelCard from '@/components/inbox/panel/MealPlanPanelCard';
import TrainingPanelCard from '@/components/inbox/panel/TrainingPanelCard';

const MEASUREMENTS_PAGE_SIZE = 50;

interface Props {
  clientPublicId: string;
  /** The participant's display name, for the not-a-client fallback. */
  participantName: string;
}

/**
 * The inbox's client panel: identity, weight / client-since cards, and the
 * active meal plan and training cards. Shares the `client-dashboard` /
 * `client-plans` / `client-measurements` query keys with `ClientDetailPage`
 * so both surfaces read one cache entry.
 */
export default function ClientSidePanel({ clientPublicId, participantName }: Props) {
  const { t } = useTranslation();
  const containerClass = 'flex min-h-0 flex-1 flex-col gap-3.5 overflow-y-auto px-4.5 py-5.5';

  const dashboardQuery = useQuery({
    queryKey: ['client-dashboard', clientPublicId],
    queryFn: () => apiClient.getClientDashboardEndpoint(clientPublicId),
    enabled: Boolean(clientPublicId),
    // A 404 is a definitive "not linked", not a transient failure worth retrying.
    retry: (failureCount, error) => getErrorStatus(error) !== 404 && failureCount < 1,
  });

  const plansQuery = useQuery({
    queryKey: ['client-plans', clientPublicId],
    queryFn: () => getClientPlans(clientPublicId),
    enabled: Boolean(clientPublicId),
  });

  const measurementsGate = useMeasurementsGate();
  const measurementsQuery = useQuery({
    queryKey: ['client-measurements', clientPublicId],
    queryFn: () => getClientMeasurements(clientPublicId, 1, MEASUREMENTS_PAGE_SIZE),
    enabled: Boolean(clientPublicId) && measurementsGate.enabled,
    placeholderData: measurementsGate.placeholderData,
  });

  if (dashboardQuery.isPending) {
    return (
      <div className={containerClass}>
        <Skeleton className="h-16 w-full" />
        <Skeleton className="h-32 w-full" />
        <Skeleton className="h-40 w-full" />
      </div>
    );
  }

  // The dashboard 404s by design for anyone without a live link (declined, cancelled or expired proposals).
  if (dashboardQuery.isError && getErrorStatus(dashboardQuery.error) === 404) {
    return <NotAClientPanel name={participantName} />;
  }

  if (dashboardQuery.isError || !dashboardQuery.data) {
    return (
      <div className="flex min-h-0 flex-1 items-center justify-center p-4">
        <p className="text-caption text-muted-foreground">{t('common.dataLoadError')}</p>
      </div>
    );
  }

  const dashboard = dashboardQuery.data;
  const plans = plansQuery.data?.plans;
  const activeNutritionPlan = plans?.find((p) => p.planType === 'Nutrition' && p.status === 'Active');
  const activeTrainingPlan = plans?.find((p) => p.planType === 'Training' && p.status === 'Active');

  return (
    <div className={containerClass}>
      <PanelIdentity dashboard={dashboard} />
      <PanelStats dashboard={dashboard} measurements={measurementsQuery.data?.items ?? []} />
      <MealPlanPanelCard
        clientPublicId={clientPublicId}
        plan={activeNutritionPlan}
        canView={plansQuery.data?.canViewNutritionPlans ?? false}
        isPending={plansQuery.isPending}
        isError={plansQuery.isError}
      />
      <TrainingPanelCard
        clientPublicId={clientPublicId}
        plan={activeTrainingPlan}
        canView={plansQuery.data?.canViewTrainingPlans ?? false}
        isPending={plansQuery.isPending}
        isError={plansQuery.isError}
        sessionsCompleted={dashboard.sessionsCompletedThisWeek}
        sessionsPlanned={dashboard.sessionsPlannedThisWeek}
      />
    </div>
  );
}
