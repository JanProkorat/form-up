import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Plus } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { AutomationActionType, type AutomationRuleDto } from '@/api/automations';
import {
  useAutomationRules,
  useCreateAutomationRule,
  useDeleteAutomationRule,
  useUpdateAutomationRule,
} from '@/hooks/useAutomationQueries';
import { useTrainerQuestionnaires } from '@/hooks/useQuestionnaireQueries';
import { useInvalidateSubscription } from '@/hooks/useSubscriptionQueries';
import { getRfc7807ErrorCode, showApiError, showSuccess } from '@/lib/api-errors';
import { isSubscriptionRequired } from '@/lib/subscriptionErrors';
import PaywallDialog from '@/components/subscription/PaywallDialog';
import { cn } from '@/lib/utils';
import AutomationRuleDrawer from '@/components/automations/AutomationRuleDrawer';
import AutomationsTable from '@/components/automations/AutomationsTable';
import {
  formValuesToRequest,
  ruleToToggleRequest,
  type AutomationRuleFormValues,
} from '@/components/automations/automationRuleForm';

type ActionFilter = 'all' | AutomationActionType;

const FILTERS: { value: ActionFilter; labelKey: string }[] = [
  { value: 'all', labelKey: 'automations.filters.all' },
  { value: AutomationActionType.SendForm, labelKey: 'automations.filters.forms' },
  { value: AutomationActionType.RequestProgressPhotos, labelKey: 'automations.filters.photos' },
  { value: AutomationActionType.RequestMeasurements, labelKey: 'automations.filters.measurements' },
];

const ERROR_RULE_NOT_FOUND = 'AUTOMATION_RULE_NOT_FOUND';

/** Forms → Automations: the coach's global rules, with create, edit, toggle and delete. */
export default function AutomationsPage() {
  const { t } = useTranslation();
  const rulesQuery = useAutomationRules();
  const formsQuery = useTrainerQuestionnaires();
  const createMutation = useCreateAutomationRule();
  const updateMutation = useUpdateAutomationRule();
  const deleteMutation = useDeleteAutomationRule();

  const [filter, setFilter] = useState<ActionFilter>('all');
  const [drawer, setDrawer] = useState<{ rule?: AutomationRuleDto } | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<AutomationRuleDto | null>(null);
  const [togglingIds, setTogglingIds] = useState<ReadonlySet<string>>(new Set());
  const [paywallOpen, setPaywallOpen] = useState(false);
  const invalidateSubscription = useInvalidateSubscription();

  const formTitles = useMemo(
    () => new Map((formsQuery.data ?? []).map((form) => [form.publicId, form.title] as const)),
    [formsQuery.data],
  );

  const allRules = rulesQuery.data?.rules ?? [];
  const rules = filter === 'all' ? allRules : allRules.filter((rule) => rule.actionType === filter);

  const handleSave = async (values: AutomationRuleFormValues) => {
    const request = formValuesToRequest(values, 'global');
    const ruleId = drawer?.rule?.ruleId;
    try {
      if (ruleId) {
        await updateMutation.mutateAsync({ ruleId, request });
      } else {
        await createMutation.mutateAsync(request);
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
      // The rule is gone (deleted elsewhere): the list already refetches, so close the stale drawer.
      if (getRfc7807ErrorCode(error) === ERROR_RULE_NOT_FOUND) {
        setDrawer(null);
      }
    }
  };

  const handleToggle = (rule: AutomationRuleDto, enabled: boolean) => {
    const ruleId = rule.ruleId;
    if (!ruleId) {
      return;
    }
    setTogglingIds((current) => new Set(current).add(ruleId));
    updateMutation.mutate(
      { ruleId, request: ruleToToggleRequest(rule, enabled) },
      {
        onError: (error) => {
          if (isSubscriptionRequired(error)) {
            invalidateSubscription();
            setPaywallOpen(true);
            return;
          }
          showApiError(error, 'automations.toggleError');
        },
        onSettled: () =>
          setTogglingIds((current) => {
            const next = new Set(current);
            next.delete(ruleId);
            return next;
          }),
      },
    );
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

  return (
    <div className="flex h-full flex-col gap-4">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="flex flex-col gap-1.5">
          <h1 className="text-display font-semibold text-ink">{t('automations.title')}</h1>
          <p className="text-copy text-muted-foreground">{t('automations.subtitle')}</p>
        </div>
        <Button type="button" size="lg" className="gap-1.75 px-3.5 font-semibold" onClick={() => setDrawer({})}>
          <Plus aria-hidden="true" />
          {t('automations.newRule')}
        </Button>
      </div>

      <div className="flex flex-wrap items-center justify-between gap-3">
        <div role="group" aria-label={t('automations.filters.label')} className="flex flex-wrap gap-2">
          {FILTERS.map(({ value, labelKey }) => (
            <button
              key={value}
              type="button"
              aria-pressed={filter === value}
              onClick={() => setFilter(value)}
              className={cn(
                'h-9 rounded-full border px-4 text-body font-medium outline-none transition-colors focus-visible:ring-3 focus-visible:ring-ring/50',
                filter === value
                  ? 'border-primary bg-primary text-primary-foreground'
                  : 'border-line bg-card text-ink-2 hover:bg-sunken',
              )}
            >
              {t(labelKey)}
            </button>
          ))}
        </div>
        <span className="text-meta text-muted-foreground">{t('automations.timeZoneNote')}</span>
      </div>

      <div className="flex min-h-0 flex-col overflow-hidden rounded-2xl border border-line bg-card shadow-panel">
        <div className="min-h-0 overflow-y-auto [&_[data-slot=table-container]]:overflow-visible">
          <AutomationsTable
            rules={rules}
            formTitles={formTitles}
            isPending={rulesQuery.isPending}
            isError={rulesQuery.isError}
            onRetry={() => void rulesQuery.refetch()}
            togglingIds={togglingIds}
            onEdit={(rule) => setDrawer({ rule })}
            onToggle={handleToggle}
            onDelete={setDeleteTarget}
          />
        </div>
        {!rulesQuery.isPending && !rulesQuery.isError && (
          <p className="border-t border-line px-4 py-3 text-meta text-muted-foreground">
            {t('automations.footer', { count: rules.length })}
          </p>
        )}
      </div>

      {drawer && (
        <AutomationRuleDrawer
          open
          onOpenChange={(open) => {
            if (!open) {
              setDrawer(null);
            }
          }}
          variant="global"
          rule={drawer.rule}
          onSubmit={handleSave}
          isSaving={createMutation.isPending || updateMutation.isPending}
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
