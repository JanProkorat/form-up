import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Info, Plus } from 'lucide-react';
import { Button } from '@/components/ui/button';
import type { SubscriptionPlanDto } from '@/api/admin-subscription-plans';
import { getErrorCode, getErrorStatus, getRfc7807ErrorCode, showApiError, showSuccess } from '@/lib/api-errors';
import PlanDrawer, { type PlanSaveOutcome } from '@/components/admin-plans/PlanDrawer';
import PlansTable from '@/components/admin-plans/PlansTable';
import {
  formValuesToCreateRequest,
  formValuesToUpdateRequest,
  type PlanFormValues,
} from '@/components/admin-plans/planForm';
import {
  useAdminPlans,
  useCreatePlan,
  useStopOfferingPlan,
  useUpdatePlan,
} from '@/components/admin-plans/useAdminPlansQueries';

const ERROR_CODE_TAKEN = 'SUBSCRIPTION_PLAN_CODE_ALREADY_EXISTS';
const ERROR_PLAN_NOT_FOUND = 'SUBSCRIPTION_PLAN_NOT_FOUND';

const errorCodeOf = (error: unknown): string | null => getRfc7807ErrorCode(error) ?? getErrorCode(error);

/** True when the plan was removed elsewhere; the list refetches, so the stale drawer should close. */
const isPlanGone = (error: unknown): boolean =>
  getErrorStatus(error) === 404 && errorCodeOf(error) === ERROR_PLAN_NOT_FOUND;

/** Form Up admin → Plans: the plan catalogue, with create, edit and stop offering. */
export default function AdminPlansPage() {
  const { t } = useTranslation();
  const plansQuery = useAdminPlans();
  const createPlan = useCreatePlan();
  const updatePlan = useUpdatePlan();
  const stopOffering = useStopOfferingPlan();
  const [drawer, setDrawer] = useState<{ plan?: SubscriptionPlanDto } | null>(null);

  const plans = plansQuery.data ?? [];

  const handleSave = async (values: PlanFormValues): Promise<PlanSaveOutcome> => {
    const saved = drawer?.plan;
    try {
      if (saved?.code) {
        await updatePlan.mutateAsync({ code: saved.code, body: formValuesToUpdateRequest(values, saved) });
      } else {
        await createPlan.mutateAsync(formValuesToCreateRequest(values));
      }
      showSuccess('adminPlans.saveSuccess');
      setDrawer(null);
      return 'saved';
    } catch (error) {
      if (getErrorStatus(error) === 409 && errorCodeOf(error) === ERROR_CODE_TAKEN) {
        return 'codeTaken';
      }
      showApiError(error, 'adminPlans.saveError');
      if (isPlanGone(error)) {
        setDrawer(null);
      }
      return 'error';
    }
  };

  const handleStopOffering = (plan: SubscriptionPlanDto) => {
    const code = plan.code;
    if (!code) {
      return;
    }
    stopOffering.mutate(code, {
      onSuccess: () => {
        showSuccess('adminPlans.stopSuccess');
        setDrawer((current) => (current?.plan?.code === code ? null : current));
      },
      onError: (error) => {
        showApiError(error, 'adminPlans.stopError');
        if (isPlanGone(error)) {
          setDrawer((current) => (current?.plan?.code === code ? null : current));
        }
      },
    });
  };

  return (
    <div className="flex h-full flex-col gap-4">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="flex flex-col gap-1.5">
          <h1 className="text-display font-semibold text-ink">{t('adminPlans.title')}</h1>
          <p className="text-copy text-muted-foreground">{t('adminPlans.subtitle')}</p>
        </div>
        <Button type="button" size="lg" className="gap-1.75 px-3.5 font-semibold" onClick={() => setDrawer({})}>
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
            onEdit={(plan) => setDrawer({ plan })}
            onStopOffering={handleStopOffering}
          />
        </div>
      </div>

      <p className="flex items-center gap-2 text-body text-muted-foreground">
        <Info className="size-4 shrink-0" aria-hidden="true" />
        {t('adminPlans.note')}
      </p>

      {drawer && (
        <PlanDrawer
          open
          onOpenChange={(open) => {
            if (!open) {
              setDrawer(null);
            }
          }}
          plan={drawer.plan}
          onSubmit={handleSave}
          onStopOffering={handleStopOffering}
          isSaving={createPlan.isPending || updatePlan.isPending || stopOffering.isPending}
        />
      )}
    </div>
  );
}
