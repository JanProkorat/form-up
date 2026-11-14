import { Link, useNavigate, useParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { ChevronLeft } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { getErrorCode, getErrorStatus, getRfc7807ErrorCode, showApiError, showSuccess } from '@/lib/api-errors';
import PlanEditorForm, { PLANS_PATH, type PlanSaveOutcome } from '@/components/admin-plans/PlanEditorForm';
import {
  formValuesToCreateRequest,
  formValuesToUpdateRequest,
  type PlanFormValues,
} from '@/components/admin-plans/planForm';
import { useAdminPlans, useCreatePlan, useUpdatePlan } from '@/components/admin-plans/useAdminPlansQueries';

const ERROR_CODE_TAKEN = 'SUBSCRIPTION_PLAN_CODE_ALREADY_EXISTS';

const errorCodeOf = (error: unknown): string | null => getRfc7807ErrorCode(error) ?? getErrorCode(error);

const BACK_BUTTON_CLASS =
  'flex size-9 shrink-0 items-center justify-center rounded-lg border border-border bg-card text-ink transition-colors outline-none hover:bg-muted focus-visible:ring-3 focus-visible:ring-ring/50';

/**
 * Form Up admin → Plans → one plan, or a new one. The route segment is the plan code (plans have no id),
 * so the saved plan is found in the plan list.
 */
export default function AdminPlanEditorPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { planId } = useParams();
  const isNew = planId === undefined;
  const plansQuery = useAdminPlans();
  const createPlan = useCreatePlan();
  const updatePlan = useUpdatePlan();

  const plan = isNew ? undefined : plansQuery.data?.find((candidate) => candidate.code === planId);

  const handleSave = async (values: PlanFormValues): Promise<PlanSaveOutcome> => {
    try {
      if (plan?.code) {
        await updatePlan.mutateAsync({ code: plan.code, body: formValuesToUpdateRequest(values, plan) });
      } else {
        await createPlan.mutateAsync(formValuesToCreateRequest(values));
      }
      showSuccess('adminPlans.saveSuccess');
      navigate(PLANS_PATH);
      return 'saved';
    } catch (error) {
      if (getErrorStatus(error) === 409 && errorCodeOf(error) === ERROR_CODE_TAKEN) {
        return 'codeTaken';
      }
      showApiError(error, 'adminPlans.saveError');
      return 'error';
    }
  };

  const backLink = (
    <Link to={PLANS_PATH} aria-label={t('adminPlans.editor.back')} className={BACK_BUTTON_CLASS}>
      <ChevronLeft className="size-4.5" aria-hidden="true" />
    </Link>
  );

  if (!isNew && plansQuery.isPending) {
    return (
      <div className="flex flex-col gap-5.5">
        {backLink}
        <Skeleton className="h-16 w-full max-w-md rounded-xl" />
        <Skeleton className="h-72 w-full rounded-2xl" />
      </div>
    );
  }

  if (!isNew && plansQuery.isError) {
    return (
      <div className="flex flex-col items-start gap-3">
        {backLink}
        <p className="text-body text-muted-foreground">{t('adminPlans.loadError')}</p>
        <Button type="button" variant="outline" size="sm" onClick={() => void plansQuery.refetch()}>
          {t('adminPlans.retry')}
        </Button>
      </div>
    );
  }

  if (!isNew && !plan) {
    return (
      <div className="flex flex-col items-start gap-3" data-testid="plan-not-found">
        {backLink}
        <h1 className="text-display font-semibold text-ink">{t('adminPlans.editor.notFoundTitle')}</h1>
        <p className="text-copy text-muted-foreground">{t('adminPlans.editor.notFoundBody')}</p>
        <Button asChild variant="outline" size="lg">
          <Link to={PLANS_PATH}>{t('adminPlans.editor.backToPlans')}</Link>
        </Button>
      </div>
    );
  }

  return (
    <PlanEditorForm
      key={plan?.code ?? 'new'}
      plan={plan}
      onSubmit={handleSave}
      onCancel={() => navigate(PLANS_PATH)}
      isSaving={createPlan.isPending || updatePlan.isPending}
    />
  );
}
