import { useCallback, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { ArrowLeft, FileText } from 'lucide-react';
import { apiClient } from '@/api/client';
import type { GetPlanResponse, OnboardingDataDto } from '@/api/generated';
import { fetchPlanForEditor } from '@/api/plans';
import { useClientForms } from '@/components/client-forms/useClientForms';
import ClientAvatar from '@/components/clients/ClientAvatar';
import OutsidePlanPaywallDialog from '@/components/client-detail/OutsidePlanPaywallDialog';
import PlanEditorFormsTab from '@/components/client-plan-editor/forms/PlanEditorFormsTab';
import { selectPlanEditorForms } from '@/components/client-plan-editor/forms/planEditorForms';
import ClientPlanInfoPanel from '@/components/client-plan-editor/ClientPlanInfoPanel';
import PlanActionsMenu, { DeletePlanDialog, FinishPlanDialog } from '@/components/client-plan-editor/PlanActionsMenu';
import PlanEndedBanner from '@/components/client-plan-editor/PlanEndedBanner';
import TemplatesLibraryTab from '@/components/client-plan-editor/TemplatesLibraryTab';
import PlanEditor from '@/components/plan-editor/PlanEditor';
import { buildPlanMealLocks } from '@/components/plan-editor/meal-locks';
import CopyMealsDialog from '@/components/plan-templates/CopyMealsDialog';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { useClientNutritionRealtime } from '@/hooks/useClientNutritionRealtime';
import { isClosedPlan, toPlanEditorDocument, useClientPlanEditor } from '@/hooks/useClientPlanEditor';
import { useClientPlanOfflineSyncConflicts, useReviewOfflineSyncConflicts } from '@/hooks/useClientOfflineSyncConflicts';
import { getErrorStatus } from '@/lib/api-errors';
import { useToastStore } from '@/stores/toast';

interface LoadedProps {
  plan: GetPlanResponse;
  clientId: string;
  clientName: string;
  firstName?: string;
  lastName?: string;
  onboarding?: OnboardingDataDto;
  onReload: () => void;
  /** False while the plan is being refetched: its lock data and stored meals are about to change. */
  planFresh: boolean;
  /** The client is outside the coach's plan: edits, saving and publishing open the paywall instead. */
  isOutsidePlan: boolean;
}

/** Hosts the shared plan editor for one loaded client plan. Mounted once per load, so edits stay local. */
function LoadedClientPlanEditor({
  plan,
  clientId,
  clientName,
  firstName,
  lastName,
  onboarding,
  onReload,
  planFresh,
  isOutsidePlan,
}: LoadedProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [finishOpen, setFinishOpen] = useState(false);
  const [paywallOpen, setPaywallOpen] = useState(false);
  const openPaywall = useCallback(() => setPaywallOpen(true), []);
  // A closed plan stays read-only as before; the outside-plan lock only applies to a plan that could be edited.
  const outsidePlan = isOutsidePlan && !isClosedPlan(plan) ? { onBlocked: openPaywall } : undefined;
  const {
    initial,
    saveState,
    lockRejection,
    clearLockRejection,
    save,
    primaryAction,
    weekStatus,
    savedStartDate,
    canDelete,
    canFinish,
    ended,
    deletePlan,
    finishPlan,
    lifecycleBusy,
    markTemplateApplied,
  } = useClientPlanEditor(plan, clientId, openPaywall);
  // Read from the page's live plan, not the editor's adopted copy: a save response carries no lock data.
  const mealLocks = useMemo(
    () => buildPlanMealLocks(plan, firstName || clientName),
    [plan, firstName, clientName],
  );
  // The server's copy of the plan for "Restore"; withheld while a refetch is under way so a stale meal is never restored.
  const storedDocument = useMemo(() => (planFresh ? toPlanEditorDocument(plan) : null), [plan, planFresh]);
  const backToNutrition = () => navigate(`/clients/${clientId}/nutrition`);

  const planId = plan.planId ?? '';
  const addToast = useToastStore((state) => state.addToast);
  const conflictsQuery = useClientPlanOfflineSyncConflicts(clientId, planId);
  const { reviewOne, reviewAll } = useReviewOfflineSyncConflicts(clientId, planId, (kind) =>
    addToast(t(kind === 'one' ? 'planEditor.syncConflicts.reviewError' : 'planEditor.syncConflicts.reviewAllError'), 'error'),
  );

  const clientFirstName = firstName || clientName;
  const formsQuery = useClientForms(clientId);
  const hasNewForm = selectPlanEditorForms(formsQuery.data?.current).some((item) => item.isNew);

  return (
    <>
    <PlanEditor
      initial={initial}
      outsidePlan={outsidePlan}
      extraSideTabs={[
        {
          id: 'forms',
          label: t('clientForms.tab'),
          icon: FileText,
          indicator: hasNewForm ? { label: t('clientForms.newAnswer') } : undefined,
          content: (
            <PlanEditorFormsTab
              clientId={clientId}
              clientFirstName={clientFirstName}
              onboarding={onboarding}
              forms={formsQuery}
            />
          ),
        },
      ]}
      syncConflicts={{
        conflicts: conflictsQuery.data ?? [],
        clientName: firstName || clientName,
        planVersion: plan.version,
        reviewingId: reviewOne.isPending ? (reviewOne.variables ?? null) : null,
        reviewingAll: reviewAll.isPending,
        onReview: (conflictId) => reviewOne.mutateAsync(conflictId).then(() => true, () => false),
        onReviewAll: () => reviewAll.mutateAsync().then(() => true, () => false),
      }}
      renderCopyMeals={(slot) => (
        <CopyMealsDialog
          open={slot.open}
          onOpenChange={slot.onOpenChange}
          currentTemplateId={undefined}
          onApply={slot.onApply}
        />
      )}
      mealLocks={mealLocks}
      readOnly={isClosedPlan(plan)}
      readOnlyNotice={t('clientPlanEditor.readOnly')}
      breadcrumb={{
        label: clientName,
        onNavigate: backToNutrition,
        leading: <ClientAvatar firstName={firstName} lastName={lastName} className="size-7.5" />,
      }}
      primaryAction={primaryAction}
      secondarySave
      renderInfoPanel={(context) => (
        <ClientPlanInfoPanel
          {...context}
          clientId={clientId}
          clientFirstName={firstName || clientName}
          onboarding={onboarding}
          savedStartDate={savedStartDate}
          sourceTemplateId={plan.sourceTemplateId}
          outsidePlanNote={outsidePlan !== undefined}
        />
      )}
      infoTabLabel={t('clientPlanEditor.info.tab')}
      libraryExtraTab={{
        label: t('clientPlanEditor.templates.tab'),
        render: (context) => (
          <TemplatesLibraryTab
            context={context}
            weekStatus={weekStatus}
            onboarding={onboarding}
            clientFirstName={firstName || clientName}
            onTemplateApplied={markTemplateApplied}
            onBlocked={outsidePlan?.onBlocked}
          />
        ),
      }}
      weekStatus={weekStatus}
      allowWeekRemoval
      renderHeaderActions={(host) =>
        canDelete || canFinish ? (
          <>
            <PlanActionsMenu
              canDelete={canDelete}
              canFinish={canFinish}
              disabled={host.busy || lifecycleBusy}
              onDelete={() => setDeleteOpen(true)}
              onFinish={() => setFinishOpen(true)}
            />
            <DeletePlanDialog
              open={deleteOpen}
              busy={lifecycleBusy}
              onCancel={() => setDeleteOpen(false)}
              onConfirm={() => {
                void deletePlan().then((deleted) => {
                  setDeleteOpen(false);
                  if (deleted) {
                    host.leaveWithoutPrompt(backToNutrition);
                  }
                });
              }}
            />
            <FinishPlanDialog
              open={finishOpen}
              busy={host.busy || lifecycleBusy}
              onCancel={() => setFinishOpen(false)}
              onConfirm={() => {
                void finishPlan(host.flush).then((finished) => {
                  setFinishOpen(false);
                  if (finished) {
                    host.leaveWithoutPrompt(backToNutrition);
                  }
                });
              }}
            />
          </>
        ) : null
      }
      renderBanner={(host) =>
        ended ? <PlanEndedBanner disabled={host.busy || lifecycleBusy} onFinish={() => setFinishOpen(true)} /> : null
      }
      saveState={saveState}
      lockRejection={lockRejection}
      onClearLockRejection={clearLockRejection}
      storedDocument={storedDocument}
      onSave={save}
      onReload={onReload}
    />
    <OutsidePlanPaywallDialog open={paywallOpen} onOpenChange={setPaywallOpen} firstName={clientFirstName} />
    </>
  );
}

function NotFound({ clientId }: { clientId: string }) {
  const { t } = useTranslation();
  return (
    <div className="flex h-full flex-col gap-4 p-6">
      <Button asChild variant="ghost" size="sm" className="w-fit gap-1.5 px-0 font-semibold text-ink">
        <Link to={`/clients/${clientId}/nutrition`}>
          <ArrowLeft className="size-4" aria-hidden="true" />
          {t('clientPlanEditor.backToNutrition')}
        </Link>
      </Button>
      <div className="flex flex-col items-start gap-1">
        <p className="text-copy font-bold text-ink">{t('clientPlanEditor.notFound.title')}</p>
        <p className="text-body text-muted-foreground">{t('clientPlanEditor.notFound.body')}</p>
      </div>
    </div>
  );
}

/** The client plan editor page: loads the plan and the client, and hosts the shared editor. */
export default function ClientPlanEditorPage() {
  const { t } = useTranslation();
  const { clientId = '', planId = '' } = useParams<{ clientId: string; planId: string }>();
  const [reloadCount, setReloadCount] = useState(0);

  const planQuery = useQuery({
    queryKey: ['nutrition-plan-detail', planId],
    queryFn: () => fetchPlanForEditor(planId),
    enabled: Boolean(planId),
    // The editor captures its copy once, so it must start from the server's current version.
    refetchOnMount: 'always',
    retry: (failureCount, error) => {
      const status = getErrorStatus(error);
      return status !== 403 && status !== 404 && failureCount < 1;
    },
  });
  const dashboardQuery = useQuery({
    queryKey: ['client-dashboard', clientId],
    queryFn: () => apiClient.getClientDashboardEndpoint(clientId),
    enabled: Boolean(clientId),
  });
  // Refetches the plan when the client logs a meal, so lock states update; edits live in the editor's own copy.
  useClientNutritionRealtime(clientId);

  if (planQuery.isError) {
    const status = getErrorStatus(planQuery.error);
    if (status === 404 || status === 403) {
      return <NotFound clientId={clientId} />;
    }
    return (
      <div className="flex h-full flex-col items-start gap-3 p-6">
        <p className="text-body text-muted-foreground">{t('common.loadError')}</p>
        <Button type="button" variant="outline" size="sm" onClick={() => void planQuery.refetch()}>
          {t('clients.retry')}
        </Button>
      </div>
    );
  }

  // A cached copy shows at once but may be stale; wait for the fetch made on mount.
  if (!planQuery.isFetchedAfterMount || !planQuery.data || dashboardQuery.isPending) {
    return (
      <div className="flex h-full flex-col gap-4 p-6">
        <Skeleton className="h-10 w-80" />
        <Skeleton className="h-full w-full rounded-2xl" />
      </div>
    );
  }

  const plan = planQuery.data;
  if (plan.clientId !== clientId) {
    return <NotFound clientId={clientId} />;
  }

  const dashboard = dashboardQuery.data;
  const clientName =
    [dashboard?.firstName, dashboard?.lastName].filter(Boolean).join(' ') || t('clientPlanEditor.clientFallback');

  return (
    <LoadedClientPlanEditor
      key={`${planId}:${reloadCount}`}
      plan={plan}
      clientId={clientId}
      clientName={clientName}
      firstName={dashboard?.firstName}
      lastName={dashboard?.lastName}
      onboarding={dashboard?.onboarding}
      planFresh={!planQuery.isFetching}
      isOutsidePlan={dashboard?.isOutsidePlan === true}
      onReload={() => {
        void planQuery.refetch().then(() => setReloadCount((count) => count + 1));
      }}
    />
  );
}
