import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { Utensils } from 'lucide-react';
import { Skeleton } from '@/components/ui/skeleton';
import PlanCardHeader from '@/components/client-detail/PlanCardHeader';
import { getPlan } from '@/api/plans';
import { computeMacroShares } from '@/lib/client-metrics';
import type { ClientPlanItem } from '@/api/generated';

interface Props {
  /** The client's active Nutrition-type plan summary, or undefined if none. */
  plan?: ClientPlanItem;
  /** The client the card belongs to; the "Open plan" link goes to that client's Nutrition tab. */
  clientId: string;
  /** Whether the caller's link grants the nutrition domain (ListClientPlansResponse.canViewNutritionPlans). */
  canView: boolean;
  /** Whether the plans-list query (which resolves `plan`/`canView`) is still loading. */
  isPending: boolean;
  isError: boolean;
}

/**
 * "Meal plan" card. Kcal and the macro split aren't on the plans-list
 * response, so a second fetch loads the full plan for `globalSettings` once
 * we know which plan is active.
 */
export default function MealPlanCard({ plan, clientId, canView, isPending, isError }: Props) {
  const { t } = useTranslation();
  const headingId = useId();

  const detailQuery = useQuery({
    queryKey: ['nutrition-plan-detail', plan?.planId],
    queryFn: () => getPlan(plan!.planId!),
    enabled: canView && Boolean(plan?.planId),
  });

  const globalSettings = detailQuery.data?.globalSettings;
  const dailyKcal = globalSettings?.dailyKcal;
  const shares = computeMacroShares(dailyKcal, globalSettings?.proteinGrams, globalSettings?.carbsGrams, globalSettings?.fatGrams);

  let macroDetail: string | undefined;
  if (shares) {
    macroDetail = t('clientDetail.overview.mealPlan.detail', {
      kcal: dailyKcal,
      carb: shares.carbPct,
      protein: shares.proteinPct,
      fat: shares.fatPct,
    });
  }

  return (
    <section
      aria-labelledby={headingId}
      className="flex h-full flex-col gap-3 rounded-2xl border border-border bg-card p-4"
    >
      <PlanCardHeader
        headingId={headingId}
        tone="nutrition"
        openPlanTo={canView && plan ? `/clients/${clientId}/nutrition` : undefined}
        label={t('clientDetail.overview.mealPlan.label')}
        heading={t('clientDetail.overview.mealPlan.heading')}
      />

      {isPending && <Skeleton className="h-10 w-full" />}

      {!isPending && isError && <p className="text-caption text-muted-foreground">{t('common.loadError')}</p>}

      {!isPending && !isError && (!canView || !plan) && (
        <p className="text-caption text-muted-foreground">{t('clientDetail.prehled.noActivePlan.nutrition')}</p>
      )}

      {!isPending && !isError && canView && plan && (
        <>
          <div className="flex items-center gap-3">
            <div className="flex size-10 shrink-0 items-center justify-center rounded-lg bg-nutrition-soft">
              <Utensils className="size-5 text-nutrition-ink" aria-hidden="true" />
            </div>
            <div className="flex min-w-0 flex-col gap-0.5">
              <span className="truncate text-body font-bold text-ink">{plan.name}</span>
              <span className="text-caption text-muted-foreground">
                {detailQuery.isError
                  ? t('common.loadError')
                  : detailQuery.isPending
                    ? t('common.loading')
                    : (macroDetail ?? '—')}
              </span>
            </div>
          </div>

          {shares && (
            <div className="flex flex-col gap-1.5">
              <div className="flex h-1 gap-0.5" role="img" aria-label={t('clientDetail.overview.mealPlan.macrosCaption')}>
                <div className="rounded-full bg-macro-protein" style={{ flexGrow: Math.max(shares.proteinPct, 1) }} />
                <div className="rounded-full bg-macro-carbs" style={{ flexGrow: Math.max(shares.carbPct, 1) }} />
                <div className="rounded-full bg-macro-fat" style={{ flexGrow: Math.max(shares.fatPct, 1) }} />
              </div>
              <span className="text-caption text-muted-foreground">{t('clientDetail.overview.mealPlan.macrosCaption')}</span>
            </div>
          )}
        </>
      )}
    </section>
  );
}
