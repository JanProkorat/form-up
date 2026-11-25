import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { MoreHorizontal, Utensils } from 'lucide-react';
import { Button } from '@/components/ui/button';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import SaveAsTemplateDialog from '@/components/client-detail/nutrition/SaveAsTemplateDialog';
import { copyNutritionPlan } from '@/api/plans';
import { showApiError, showSuccess } from '@/lib/api-errors';
import { formatSignedNumber } from '@/lib/client-metrics';
import { planEndDate } from '@/components/client-plan-editor/clientPlanDates';
import type { ClientPlanItem } from '@/api/generated';
import {
  diffDays,
  formatShortDate,
  parseCalendarDate,
} from '@/components/client-detail/nutrition/nutritionPlanView';

interface Props {
  clientId: string;
  clientFirstName: string;
  /** Completed and Archived nutrition plans. */
  plans: ClientPlanItem[];
}

const DAYS_PER_WEEK = 7;

function PastPlanRow({
  plan,
  clientFirstName,
  duplicating,
  onOpen,
  onSaveAsTemplate,
  onDuplicate,
}: {
  plan: ClientPlanItem;
  clientFirstName: string;
  duplicating: boolean;
  onOpen: () => void;
  onSaveAsTemplate: () => void;
  onDuplicate: () => void;
}) {
  const { t, i18n } = useTranslation();
  const locale = i18n.language;
  const start = parseCalendarDate(plan.periodStart);
  const end = parseCalendarDate(plan.periodEnd);

  const plannedWeeks = plan.weekCount && plan.weekCount > 0 ? plan.weekCount : undefined;
  // A plan without a recorded end date (archived, never completed) falls back to its planned length.
  const shownEnd =
    end ??
    (start && plannedWeeks && plan.periodStart
      ? parseCalendarDate(planEndDate(plan.periodStart.slice(0, 10), plannedWeeks))
      : undefined);

  const dates = start && shownEnd ? `${formatShortDate(start, locale)} – ${formatShortDate(shownEnd, locale)}` : undefined;
  const weeks = start && end ? Math.max(1, Math.ceil((diffDays(end, start) + 1) / DAYS_PER_WEEK)) : plannedWeeks;
  const detail = [dates, weeks ? t('clientDetail.nutrition.past.weeks', { count: weeks }) : undefined]
    .filter(Boolean)
    .join(' · ');

  const compliance = plan.resultSummary?.compliancePercent;
  const weightDelta = plan.resultSummary?.weightDeltaKg;

  return (
    <li className="flex items-center gap-3 py-3">
      <div className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-nutrition-soft">
        <Utensils className="size-4 text-nutrition-ink" aria-hidden="true" />
      </div>
      <div className="flex min-w-0 flex-1 flex-col gap-0.5">
        <span className="truncate text-copy font-semibold text-ink">{plan.name}</span>
        <span className="truncate text-body text-muted-foreground">{detail || '—'}</span>
      </div>
      <div className="flex shrink-0 flex-col items-end gap-0.5">
        <span className="text-copy font-semibold text-ink">
          {compliance != null ? t('clientDetail.nutrition.past.percent', { value: Math.round(compliance) }) : '—'}
        </span>
        <span className="text-body text-muted-foreground">{t('clientDetail.nutrition.past.mealsTicked')}</span>
      </div>
      <div className="flex w-16 shrink-0 flex-col items-end gap-0.5">
        <span className="text-copy font-semibold text-ink">
          {weightDelta != null ? t('clientDetail.nutrition.past.kg', { value: formatSignedNumber(weightDelta) }) : '—'}
        </span>
        <span className="text-body text-muted-foreground">{t('clientDetail.nutrition.past.weight')}</span>
      </div>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button
            type="button"
            variant="ghost"
            size="icon-sm"
            aria-label={t('clientDetail.nutrition.past.rowMenu', { name: plan.name })}
          >
            <MoreHorizontal className="size-4" aria-hidden="true" />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end">
          <DropdownMenuItem onSelect={onOpen}>{t('clientDetail.nutrition.past.open')}</DropdownMenuItem>
          <DropdownMenuItem disabled={duplicating} onSelect={onDuplicate}>
            {t('clientDetail.nutrition.past.duplicate', { name: clientFirstName })}
          </DropdownMenuItem>
          <DropdownMenuItem onSelect={onSaveAsTemplate}>{t('clientDetail.nutrition.past.saveAsTemplate')}</DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </li>
  );
}

/** "Past plans" card: finished plans with how well the client followed them, and a row menu to reuse one. */
export default function PastPlansCard({ clientId, clientFirstName, plans }: Props) {
  const { t } = useTranslation();
  const headingId = useId();
  const [templateSource, setTemplateSource] = useState<{ planId: string; name: string } | undefined>();
  const queryClient = useQueryClient();
  const navigate = useNavigate();

  const duplicateMutation = useMutation({
    mutationFn: (planId: string) => copyNutritionPlan(planId, { clientId }),
    onSuccess: async (response) => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['client-plans', clientId] }),
        queryClient.invalidateQueries({ queryKey: ['client-dashboard', clientId] }),
      ]);
      showSuccess('clientDetail.nutrition.past.duplicateSuccess');
      if (response.planId) {
        navigate(`/clients/${clientId}/nutrition/plans/${response.planId}`);
      }
    },
    onError: (error) => {
      showApiError(error, 'clientDetail.nutrition.past.duplicateError');
      void queryClient.invalidateQueries({ queryKey: ['client-plans', clientId] });
    },
  });

  return (
    <section
      aria-labelledby={headingId}
      className="flex h-full flex-col gap-3 rounded-2xl border border-border bg-card p-5 shadow-panel"
    >
      <div className="flex items-baseline justify-between gap-3">
        <h2 id={headingId} className="text-card-title font-bold text-ink">
          {t('clientDetail.nutrition.past.heading')}
        </h2>
        <span className="text-body text-muted-foreground">
          {plans.length === 0
            ? t('clientDetail.nutrition.past.none')
            : t('clientDetail.nutrition.past.count', { count: plans.length })}
        </span>
      </div>

      {plans.length === 0 ? (
        <p className="text-copy text-muted-foreground">
          {t('clientDetail.nutrition.past.emptyHint', { name: clientFirstName })}
        </p>
      ) : (
        <>
          <ul className="divide-y divide-border border-t border-border">
            {plans.map((plan) => (
              <PastPlanRow
                key={plan.planId}
                plan={plan}
                clientFirstName={clientFirstName}
                duplicating={duplicateMutation.isPending}
                onOpen={() => plan.planId && navigate(`/clients/${clientId}/nutrition/plans/${plan.planId}`)}
                onSaveAsTemplate={() => plan.planId && setTemplateSource({ planId: plan.planId, name: plan.name ?? '' })}
                onDuplicate={() => plan.planId && duplicateMutation.mutate(plan.planId)}
              />
            ))}
          </ul>
          <p className="text-body text-muted-foreground">{t('clientDetail.nutrition.past.hint')}</p>
        </>
      )}

      <SaveAsTemplateDialog clientId={clientId} plan={templateSource} onClose={() => setTemplateSource(undefined)} />
    </section>
  );
}
