import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useLocation } from 'react-router-dom';
import type { UseQueryResult } from '@tanstack/react-query';
import { ChevronLeft, MessageSquare } from 'lucide-react';
import { Button } from '@/components/ui/button';
import ClientAvatar from '@/components/clients/ClientAvatar';
import ClientHeaderFacts from '@/components/client-detail/ClientHeaderFacts';
import { useClientHeaderFacts } from '@/components/client-detail/useClientHeaderFacts';
import { calculateAge, resolveOverviewRole } from '@/lib/client-metrics';
import { getClientDetailBackPath } from '@/lib/clientDetailNavigation';
import { ClientListStatus, type GetClientDashboardResponse, type GetMeasurementsResponse, type ListClientPlansResponse } from '@/api/generated';

interface Props {
  clientId: string;
  dashboard: GetClientDashboardResponse;
  plansQuery: UseQueryResult<ListClientPlansResponse>;
  measurementsQuery: UseQueryResult<GetMeasurementsResponse>;
  /** The tab row; rendered on the second line, left of the action buttons. */
  tabs: ReactNode;
}

const META_SEPARATOR = ' · ';

/**
 * Client-detail page header (board PageClientDetail, option B): back button,
 * client avatar (photo or initials), name with a status dot and a muted age / sex / height line, a facts strip
 * (goal, weight, plan weeks), then the tab row with the actions. Chat opens
 * `/inbox?client=<publicId>`.
 */
export default function ClientDetailHeader({ clientId, dashboard, plansQuery, measurementsQuery, tabs }: Props) {
  const { t } = useTranslation();
  const location = useLocation();
  const role = resolveOverviewRole(dashboard.canViewNutritionPlans, dashboard.canViewTrainingPlans);
  const facts = useClientHeaderFacts(clientId, dashboard, plansQuery, measurementsQuery);

  const age = dashboard.dateOfBirth ? calculateAge(dashboard.dateOfBirth) : undefined;
  const metaSegments = [
    age != null ? t('clientDetail.ageYears', { count: age }) : undefined,
    dashboard.onboarding?.sex ? t(`clients.values.${dashboard.onboarding.sex}`) : undefined,
    dashboard.heightCm != null ? t('clientDetail.overview.header.heightCm', { cm: dashboard.heightCm }) : undefined,
  ].filter((segment): segment is string => Boolean(segment));

  const status = dashboard.status;
  const statusLabel =
    status === ClientListStatus.Active
      ? t('clients.tabs.active')
      : status === ClientListStatus.Paused
        ? t('clients.tabs.paused')
        : status === ClientListStatus.Archived
          ? t('clients.tabs.archived')
          : null;

  return (
    <div className="flex flex-col gap-4.5">
      <div className="flex flex-wrap items-center justify-between gap-x-8 gap-y-3">
        <div className="flex min-w-0 items-center gap-2.5 md:gap-3.5">
          <Link
            to={getClientDetailBackPath(location.state)}
            aria-label={t('clientDetail.overview.backAriaLabel')}
            className="flex size-9 shrink-0 items-center justify-center rounded-lg border border-border bg-card text-ink transition-colors hover:bg-muted"
          >
            <ChevronLeft className="size-4.5" aria-hidden="true" />
          </Link>
          <span aria-hidden="true" data-testid="client-avatar" className="shrink-0">
            <ClientAvatar
              firstName={dashboard.firstName}
              lastName={dashboard.lastName}
              avatarBlobUrl={dashboard.avatarBlobUrl ?? undefined}
              className="size-10 shrink-0 md:size-16"
              fallbackClassName="bg-sunken text-body md:text-card-title font-semibold text-ink"
            />
          </span>
          <div className="flex min-w-0 flex-col gap-1">
            <div className="flex min-w-0 items-center gap-2.5">
              <h1 className="min-w-0 truncate font-display text-title font-semibold text-ink">
                {dashboard.firstName} {dashboard.lastName}
              </h1>
              {statusLabel && (
                <span
                  data-testid="client-status"
                  className="inline-flex shrink-0 items-center gap-1.5 text-meta text-muted-foreground"
                >
                  <span
                    className={`size-1.75 rounded-full ${status === ClientListStatus.Active ? 'bg-success' : 'bg-muted-foreground'}`}
                    aria-hidden="true"
                  />
                  {statusLabel}
                </span>
              )}
            </div>
            {metaSegments.length > 0 && (
              <span className="text-body text-muted-foreground">{metaSegments.join(META_SEPARATOR)}</span>
            )}
          </div>
        </div>

        <div className="w-full xl:ml-auto xl:w-auto">
          <ClientHeaderFacts clientId={clientId} dashboard={dashboard} role={role} facts={facts} />
        </div>
      </div>

      <div className="flex flex-wrap items-end justify-between gap-3 border-b border-border">
        <div className="-mb-px min-w-0 flex-1">{tabs}</div>
        <div className="flex shrink-0 items-center gap-2 pb-2">
          <Button asChild size="sm" className="gap-1.5">
            <Link to={`/inbox?client=${dashboard.clientPublicId}`}>
              <MessageSquare className="size-4" aria-hidden="true" />
              {t('clientDetail.overview.actions.chat')}
            </Link>
          </Button>
        </div>
      </div>
    </div>
  );
}
