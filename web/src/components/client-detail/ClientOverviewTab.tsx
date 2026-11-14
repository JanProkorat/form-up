import type { UseQueryResult } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import LockedFeatureCard from '@/components/client-detail/LockedFeatureCard';
import ClientOverviewStats from '@/components/client-detail/ClientOverviewStats';
import MealPlanCard from '@/components/client-detail/MealPlanCard';
import LatestWorkoutCard from '@/components/client-detail/LatestWorkoutCard';
import CheckInTrendCard from '@/components/client-detail/CheckInTrendCard';
import WeightCard from '@/components/client-detail/WeightCard';
import RecentActivityCard from '@/components/client-detail/RecentActivityCard';
import ClientProfileCard from '@/components/client-detail/ClientProfileCard';
import type { ClientOverviewRole } from '@/lib/client-metrics';
import type { GetClientDashboardResponse, ListClientPlansResponse, GetMeasurementsResponse } from '@/api/generated';

interface Props {
  clientId: string;
  dashboard: GetClientDashboardResponse;
  role: ClientOverviewRole;
  plansQuery: UseQueryResult<ListClientPlansResponse>;
  measurementsQuery: UseQueryResult<GetMeasurementsResponse>;
  /** The coach's plan has no weight and measurements: the weight card shows the paywall notice. */
  measurementsLocked?: boolean;
}

/**
 * Overview tab body: three stat cards + plan card(s) + check-in trend and
 * weight + recent activity and client profile. A link with both capabilities
 * gets the two-card layout (PageClientDetailC / D); a trainer-only or
 * nutritionist-only link gets its own domain's card at full width
 * (PageClientDetailTrainerC / D, PageClientDetailNutritionistC / D).
 */
export default function ClientOverviewTab({
  clientId,
  dashboard,
  role,
  plansQuery,
  measurementsQuery,
  measurementsLocked = false,
}: Props) {
  const { t } = useTranslation();
  const canViewNutritionPlans = plansQuery.data?.canViewNutritionPlans ?? false;
  const canViewTrainingPlans = plansQuery.data?.canViewTrainingPlans ?? false;
  const activeNutritionPlan = plansQuery.data?.plans?.find((p) => p.planType === 'Nutrition' && p.status === 'Active');
  const activeTrainingPlan = plansQuery.data?.plans?.find((p) => p.planType === 'Training' && p.status === 'Active');

  const mealPlanCard = (
    <MealPlanCard
      plan={activeNutritionPlan}
      canView={canViewNutritionPlans}
      isPending={plansQuery.isPending}
      isError={plansQuery.isError}
    />
  );
  const workoutCard = (
    <LatestWorkoutCard
      plan={activeTrainingPlan}
      canView={canViewTrainingPlans}
      isPending={plansQuery.isPending}
      isError={plansQuery.isError}
      sessionsCompleted={dashboard.sessionsCompletedThisWeek}
      sessionsPlanned={dashboard.sessionsPlannedThisWeek}
    />
  );

  return (
    <div className="flex flex-col gap-4">
      <ClientOverviewStats
        dashboard={dashboard}
        clientId={clientId}
        role={role}
        measurements={measurementsQuery.data?.items ?? []}
        measurementsPending={measurementsQuery.isPending}
        measurementsError={measurementsQuery.isError}
      />

      {role === 'both' ? (
        <div className="grid grid-cols-1 gap-3 lg:grid-cols-5">
          <div className="lg:col-span-3">{mealPlanCard}</div>
          <div className="lg:col-span-2">{workoutCard}</div>
        </div>
      ) : (
        <div>{role === 'nutritionist' ? mealPlanCard : workoutCard}</div>
      )}

      <div className="grid grid-cols-1 gap-3.5 lg:grid-cols-5">
        <div className="lg:col-span-3">
          <CheckInTrendCard clientUserId={dashboard.clientUserId ?? ''} />
        </div>
        <div className="lg:col-span-2">
          {measurementsLocked ? (
            <LockedFeatureCard feature="measurements" title={t('clientDetail.overview.weight.heading')} />
          ) : (
            <WeightCard
              dashboard={dashboard}
              measurements={measurementsQuery.data?.items ?? []}
              isPending={measurementsQuery.isPending}
              isError={measurementsQuery.isError}
              onRetry={() => void measurementsQuery.refetch()}
            />
          )}
        </div>
      </div>

      <div className="grid grid-cols-1 gap-3.5 lg:grid-cols-2">
        <RecentActivityCard clientId={clientId} role={role} />
        <ClientProfileCard onboarding={dashboard.onboarding} role={role} />
      </div>
    </div>
  );
}
