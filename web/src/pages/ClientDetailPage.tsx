import { Navigate, useLocation, useMatch, useNavigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { apiClient } from '@/api/client';
import { getClientPlans } from '@/api/client-plans';
import { getClientMeasurements } from '@/api/measurements';
import { resolveOverviewRole } from '@/lib/client-metrics';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import ClientDetailHeader from '@/components/client-detail/ClientDetailHeader';
import ClientAutomationsTab from '@/components/client-detail/automations/ClientAutomationsTab';
import ClientDetailTabs,{ type ClientDetailTab } from '@/components/client-detail/ClientDetailTabs';
import ClientOverviewTab from '@/components/client-detail/ClientOverviewTab';
import ClientNutritionTab from '@/components/client-detail/nutrition/ClientNutritionTab';
import ClientPaymentTab from '@/components/client-detail/payment/ClientPaymentTab';
import EndedClientPaymentView from '@/components/client-detail/payment/EndedClientPaymentView';
import { getErrorStatus } from '@/lib/api-errors';
import { useInvalidateClientLinkState } from '@/hooks/useClientPaymentQueries';
import { useSignalR } from '@/hooks/useSignalR';
import { useSubscription } from '@/hooks/useSubscriptionQueries';
import { useMeasurementsGate } from '@/hooks/useMeasurementsGate';
import PaywallPanel from '@/components/subscription/PaywallPanel';
import PaymentsPaywall from '@/components/client-detail/payment/PaymentsPaywall';
import OutsidePlanBanner from '@/components/subscription/OutsidePlanBanner';
import { useClientNutritionRealtime } from '@/hooks/useClientNutritionRealtime';
import ClientPhotosTab from '@/components/client-detail/photos/ClientPhotosTab';
import ClientFormsTab from '@/components/client-detail/forms/ClientFormsTab';
import { hasNewForms } from '@/components/client-detail/forms/clientFormsModel';
import { useClientForms } from '@/components/client-forms/useClientForms';

const MEASUREMENTS_PAGE_SIZE = 50;

/**
 * Client-detail page: header + URL-driven tab row. `/clients/:clientId` is the
 * Overview tab, `/clients/:clientId/nutrition` the Nutrition tab (a link
 * without the nutrition capability is redirected to Overview) and
 * `/clients/:clientId/photos` the Photos tab, `/clients/:clientId/payment` the Payment tab
 * `/clients/:clientId/automations` the Automations tab and `/clients/:clientId/forms` the Forms tab.
 */
export default function ClientDetailPage() {
  const { t } = useTranslation();
  const { clientId = '' } = useParams<{ clientId: string }>();
  const navigate = useNavigate();
  const location = useLocation();
  const isNutritionRoute = useMatch('/clients/:clientId/nutrition') !== null;
  const isPhotosRoute = useMatch('/clients/:clientId/photos') !== null;
  const isPaymentRoute = useMatch('/clients/:clientId/payment') !== null;
  const isAutomationsRoute = useMatch('/clients/:clientId/automations') !== null;
  const isFormsRoute = useMatch('/clients/:clientId/forms') !== null;
  const activeTab: ClientDetailTab = isNutritionRoute
    ? 'nutrition'
    : isPhotosRoute
      ? 'photos'
      : isPaymentRoute
        ? 'payment'
        : isAutomationsRoute
          ? 'automations'
          : isFormsRoute
            ? 'forms'
            : 'overview';

  const invalidateLinkState = useInvalidateClientLinkState(clientId);
  const subscription = useSubscription();

  const dashboardQuery = useQuery({
    queryKey: ['client-dashboard', clientId],
    queryFn: () => apiClient.getClientDashboardEndpoint(clientId),
    enabled: Boolean(clientId),
  });

  const plansQuery = useQuery({
    queryKey: ['client-plans', clientId],
    queryFn: () => getClientPlans(clientId),
    enabled: Boolean(clientId),
  });

  const measurementsGate = useMeasurementsGate();
  const measurementsQuery = useQuery({
    queryKey: ['client-measurements', clientId],
    queryFn: () => getClientMeasurements(clientId, 1, MEASUREMENTS_PAGE_SIZE),
    enabled: Boolean(clientId) && measurementsGate.enabled,
    placeholderData: measurementsGate.placeholderData,
  });

  // Shares the Forms tab's cache entry. An ended link (404) just shows no dot.
  const formsQuery = useClientForms(clientId);
  const hasNewFormAnswers = !formsQuery.isError && hasNewForms(formsQuery.data?.current);

  // The event carries no client id, so the open page refreshes its own keys every time.
  useSignalR({
    collaborationended: () => {
      void invalidateLinkState();
    },
  });

  useClientNutritionRealtime(clientId);

  if (dashboardQuery.isPending) {
    return (
      <div className="flex flex-col gap-4">
        <Skeleton className="h-16 w-full" />
        <Skeleton className="h-10 w-full" />
        <Skeleton className="h-64 w-full" />
      </div>
    );
  }

  // An ended link answers the dashboard with 404, but its kept payments are still reachable.
  if (isPaymentRoute && dashboardQuery.isError && getErrorStatus(dashboardQuery.error) === 404) {
    return <EndedClientPaymentView clientId={clientId} />;
  }

  if (dashboardQuery.isError || !dashboardQuery.data) {
    return (
      <div className="flex flex-col items-center gap-3 py-16">
        <p className="text-body text-muted-foreground">{t('common.dataLoadError')}</p>
        <Button type="button" variant="outline" onClick={() => void dashboardQuery.refetch()}>
          {t('clients.retry')}
        </Button>
      </div>
    );
  }

  const dashboard = dashboardQuery.data;

  // The layout follows the dashboard's capability flags: that query has resolved before first paint.
  const role = resolveOverviewRole(dashboard.canViewNutritionPlans, dashboard.canViewTrainingPlans);

  if (activeTab === 'nutrition' && role === 'trainer') {
    return <Navigate to={`/clients/${clientId}`} replace state={location.state} />;
  }

  function handleTabChange(tab: ClientDetailTab) {
    const suffix = tab === 'overview' ? '' : `/${tab}`;
    navigate(`/clients/${clientId}${suffix}`, { state: location.state });
  }

  return (
    <div className="flex flex-col gap-4">
      {dashboard.isOutsidePlan === true && (
        <OutsidePlanBanner
          clientName={`${dashboard.firstName ?? ''} ${dashboard.lastName ?? ''}`.trim()}
          planName={subscription.planName}
          clientLimit={subscription.clientLimit}
          overCapClientCount={subscription.overCapClientCount}
          freeSlotCount={subscription.freeSlotCount}
        />
      )}
      <ClientDetailHeader
        clientId={clientId}
        dashboard={dashboard}
        plansQuery={plansQuery}
        measurementsQuery={measurementsQuery}
        tabs={
          <ClientDetailTabs
            activeTab={activeTab}
            onTabChange={handleTabChange}
            canViewNutritionPlans={role !== 'trainer'}
            canViewTrainingPlans={role !== 'nutritionist'}
            hasNewForms={hasNewFormAnswers}
          />
        }
      />

      {activeTab === 'nutrition' ? (
        <ClientNutritionTab clientId={clientId} dashboard={dashboard} />
      ) : activeTab === 'photos' ? (
        // The tab's queries live in the child, so a locked plan never sends them.
        subscription.isEntitled('canUseProgressPhotos') ? (
          <ClientPhotosTab
            clientId={clientId}
            dashboard={dashboard}
            plans={plansQuery.data?.plans}
          />
        ) : (
          <PaywallPanel feature="progressPhotos" />
        )
      ) : activeTab === 'payment' ? (
        subscription.isEntitled('canUsePayments') ? (
          <ClientPaymentTab clientId={clientId} dashboard={dashboard} />
        ) : (
          <PaymentsPaywall />
        )
      ) : activeTab === 'automations' ? (
        <ClientAutomationsTab clientId={clientId} dashboard={dashboard} />
      ) : activeTab === 'forms' ? (
        <ClientFormsTab clientId={clientId} dashboard={dashboard} />
      ) : (
        <ClientOverviewTab
          clientId={clientId}
          dashboard={dashboard}
          role={role}
          plansQuery={plansQuery}
          measurementsQuery={measurementsQuery}
          measurementsLocked={measurementsGate.locked}
        />
      )}
    </div>
  );
}
