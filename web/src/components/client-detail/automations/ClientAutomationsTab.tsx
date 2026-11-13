import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Plus } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Skeleton } from '@/components/ui/skeleton';
import { AutomationRuleSource, type ClientAutomationItem } from '@/api/automations';
import type { GetClientDashboardResponse } from '@/api/generated';
import {
  useAutomationRules,
  useClientAutomations,
  useCreateClientAutomationRule,
  useDeleteAutomationRule,
  useOverrideClientAutomation,
  usePauseClientAutomation,
  useResetClientAutomation,
  useUpdateAutomationRule,
} from '@/hooks/useAutomationQueries';
import { useTrainerQuestionnaires } from '@/hooks/useQuestionnaireQueries';
import { useInvalidateSubscription, useSubscription } from '@/hooks/useSubscriptionQueries';
import { getErrorStatus, getRfc7807ErrorCode, showApiError, showSuccess } from '@/lib/api-errors';
import { isSubscriptionRequired } from '@/lib/subscriptionErrors';
import PaywallDialog from '@/components/subscription/PaywallDialog';
import AutomationRuleDrawer from '@/components/automations/AutomationRuleDrawer';
import {
  clientItemToRule,
  formValuesToOverrideRequest,
  formValuesToPauseRequest,
  formValuesToRequest,
  overrideChanged,
  pauseChanged,
  pauseToFormValues,
  ruleToFormValues,
  ruleToToggleRequest,
  type AutomationRuleFormValues,
} from '@/components/automations/automationRuleForm';
import ClientAutomationsSummary from '@/components/client-detail/automations/ClientAutomationsSummary';
import ClientAutomationsTable from '@/components/client-detail/automations/ClientAutomationsTable';

interface Props {
  clientId: string;
  dashboard: GetClientDashboardResponse;
}

type DrawerState = { variant: 'override'; item: ClientAutomationItem } | { variant: 'clientOnly'; item?: ClientAutomationItem };

const ERROR_RULE_NOT_FOUND = 'AUTOMATION_RULE_NOT_FOUND';
const SKELETON_ROW_COUNT = 4;

/** The values the override drawer starts from, for telling which of its two saves are needed. */
function overrideInitialValues(item: ClientAutomationItem): AutomationRuleFormValues {
  return { ...ruleToFormValues(clientItemToRule(item)), ...pauseToFormValues(item) };
}

/**
 * Automations tab of the client detail page: the rules that apply to this client with their source,
 * next send and last result, a pause switch per rule, per-client customisation, client-only rules,
 * and a 30-day summary.
 */
export default function ClientAutomationsTab({ clientId, dashboard }: Props) {
  const { t } = useTranslation();
  const query = useClientAutomations(clientId);
  const rulesQuery = useAutomationRules();
  const formsQuery = useTrainerQuestionnaires();
  const createMutation = useCreateClientAutomationRule(clientId);
  const overrideMutation = useOverrideClientAutomation(clientId);
  const pauseMutation = usePauseClientAutomation(clientId);
  const resetMutation = useResetClientAutomation(clientId);
  const updateMutation = useUpdateAutomationRule();
  const deleteMutation = useDeleteAutomationRule();

  const [drawer, setDrawer] = useState<DrawerState | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<ClientAutomationItem | null>(null);
  const [busyIds, setBusyIds] = useState<ReadonlySet<string>>(new Set());
  const [paywallOpen, setPaywallOpen] = useState(false);
  const { isEntitled } = useSubscription();
  const invalidateSubscription = useInvalidateSubscription();

  const clientName = dashboard.firstName?.trim() || t('clientDetail.photos.clientFallback');

  const parentRules = useMemo(
    () => new Map((rulesQuery.data?.rules ?? []).flatMap((rule) => (rule.ruleId ? [[rule.ruleId, rule] as const] : []))),
    [rulesQuery.data],
  );
  const formTitles = useMemo(
    () => new Map((formsQuery.data ?? []).map((form) => [form.publicId, form.title] as const)),
    [formsQuery.data],
  );

  const setBusy = (ruleId: string, busy: boolean) =>
    setBusyIds((current) => {
      const next = new Set(current);
      if (busy) {
        next.add(ruleId);
      } else {
        next.delete(ruleId);
      }
      return next;
    });

  const handleToggle = (item: ClientAutomationItem, on: boolean) => {
    const ruleId = item.ruleId;
    if (!ruleId) {
      return;
    }
    setBusy(ruleId, true);
    const callbacks = {
      onError: (error: unknown) => {
        if (isSubscriptionRequired(error)) {
          invalidateSubscription();
          setPaywallOpen(true);
          return;
        }
        showApiError(error, 'clientDetail.automations.toggleError');
      },
      onSettled: () => setBusy(ruleId, false),
    };
    if (item.source === AutomationRuleSource.OnlyThisClient) {
      updateMutation.mutate({ ruleId, request: ruleToToggleRequest(clientItemToRule(item), on) }, callbacks);
    } else {
      pauseMutation.mutate({ ruleId, request: { isPaused: !on } }, callbacks);
    }
  };

  const handleReset = (item: ClientAutomationItem) => {
    const ruleId = item.ruleId;
    if (!ruleId) {
      return;
    }
    setBusy(ruleId, true);
    resetMutation.mutate(ruleId, {
      onSuccess: () => showSuccess('clientDetail.automations.resetSuccess'),
      onError: (error) => showApiError(error, 'clientDetail.automations.resetError'),
      onSettled: () => setBusy(ruleId, false),
    });
  };

  const handleDelete = () => {
    const ruleId = deleteTarget?.ruleId;
    if (!ruleId) {
      return;
    }
    deleteMutation.mutate(ruleId, {
      onSuccess: () => showSuccess('automations.deleteSuccess'),
      onError: (error) => showApiError(error, 'automations.deleteError'),
      onSettled: () => setDeleteTarget(null),
    });
  };

  /** Resolves when the save is done and never rejects; a failed call shows its error and keeps the drawer open. */
  const handleSave = async (values: AutomationRuleFormValues) => {
    if (!drawer) {
      return;
    }
    try {
      if (drawer.variant === 'override') {
        const ruleId = drawer.item.ruleId;
        if (!ruleId) {
          return;
        }
        const initial = overrideInitialValues(drawer.item);
        // Two calls, each only when its part changed: a pause-only change must not create a customisation.
        if (overrideChanged(initial, values)) {
          await overrideMutation.mutateAsync({ ruleId, request: formValuesToOverrideRequest(values) });
        }
        if (pauseChanged(initial, values)) {
          await pauseMutation.mutateAsync({ ruleId, request: formValuesToPauseRequest(values) });
        }
      } else {
        const request = formValuesToRequest(values, 'clientOnly');
        const ruleId = drawer.item?.ruleId;
        if (ruleId) {
          await updateMutation.mutateAsync({ ruleId, request });
        } else {
          await createMutation.mutateAsync(request);
        }
      }
      showSuccess('automations.saveSuccess');
      setDrawer(null);
    } catch (error) {
      if (isSubscriptionRequired(error)) {
        // Stale plan data: refetch it and explain with the paywall instead of a toast.
        invalidateSubscription();
        setPaywallOpen(true);
        return;
      }
      showApiError(error, 'automations.saveError');
      // The rule is gone (deleted elsewhere): the lists already refetch, so close the stale drawer.
      if (getRfc7807ErrorCode(error) === ERROR_RULE_NOT_FOUND) {
        setDrawer(null);
      }
    }
  };

  const handleOpen = (item: ClientAutomationItem) =>
    setDrawer(item.source === AutomationRuleSource.OnlyThisClient ? { variant: 'clientOnly', item } : { variant: 'override', item });

  if (query.isPending) {
    return (
      <div className="flex flex-col gap-4">
        <Skeleton className="h-10 w-full" />
        {Array.from({ length: SKELETON_ROW_COUNT }, (_, index) => (
          <Skeleton key={index} className="h-14 w-full" />
        ))}
      </div>
    );
  }

  if (query.isError) {
    const noAccess = getErrorStatus(query.error) === 403;
    return (
      <div role="alert" className="flex flex-col items-center gap-3 py-16">
        <p className="text-body text-muted-foreground">
          {noAccess ? t('clientDetail.automations.noAccess', { name: clientName }) : t('clientDetail.automations.loadError')}
        </p>
        {!noAccess && (
          <Button type="button" variant="outline" onClick={() => void query.refetch()}>
            {t('automations.retry')}
          </Button>
        )}
      </div>
    );
  }

  const items = query.data.items ?? [];

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-copy text-muted-foreground">
          {t('clientDetail.automations.introBefore')}
          <Link to="/automations" className="font-semibold text-ink underline underline-offset-2">
            {t('clientDetail.automations.introLink')}
          </Link>
          {t('clientDetail.automations.introAfter', { name: clientName })}
        </p>
        <Button type="button" size="lg" className="gap-1.75 px-3.5 font-semibold" onClick={() => setDrawer({ variant: 'clientOnly' })}>
          <Plus aria-hidden="true" />
          {t('clientDetail.automations.addRule', { name: clientName })}
        </Button>
      </div>

      <div className="overflow-hidden rounded-2xl border border-line bg-card shadow-panel">
        <div className="overflow-x-auto">
          {items.length === 0 ? (
            <p className="px-4 py-10 text-center text-body text-muted-foreground">
              {t('clientDetail.automations.empty', { name: clientName })}
            </p>
          ) : (
            <ClientAutomationsTable
              items={items}
              clientName={clientName}
              parentRules={parentRules}
              formTitles={formTitles}
              busyIds={busyIds}
              checkInOverrideLocked={!isEntitled('canUsePerClientCheckInConfig')}
              onOpen={handleOpen}
              onToggle={handleToggle}
              onReset={handleReset}
              onDelete={setDeleteTarget}
            />
          )}
        </div>
      </div>

      <ClientAutomationsSummary summary={query.data.last30Days} clientName={clientName} />

      {drawer && (
        <AutomationRuleDrawer
          open
          onOpenChange={(open) => {
            if (!open) {
              setDrawer(null);
            }
          }}
          variant={drawer.variant}
          rule={drawer.item ? clientItemToRule(drawer.item) : undefined}
          pause={drawer.variant === 'override' ? drawer.item : undefined}
          clientName={clientName}
          onSubmit={handleSave}
          isSaving={
            createMutation.isPending || overrideMutation.isPending || pauseMutation.isPending || updateMutation.isPending
          }
        />
      )}

      <PaywallDialog open={paywallOpen} onOpenChange={setPaywallOpen} feature="checkIns" />

      <Dialog open={deleteTarget !== null} onOpenChange={(open) => !open && setDeleteTarget(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t('automations.deleteDialog.title')}</DialogTitle>
            <DialogDescription>
              {t('automations.deleteDialog.description', { name: deleteTarget?.name ?? '' })}
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setDeleteTarget(null)}>
              {t('common.cancel')}
            </Button>
            <Button type="button" variant="destructive" disabled={deleteMutation.isPending} onClick={handleDelete}>
              {t('automations.deleteDialog.confirm')}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
