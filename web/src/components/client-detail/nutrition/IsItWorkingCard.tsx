import { useId, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { getClientMeasurements } from '@/api/measurements';
import { useMeasurementsGate } from '@/hooks/useMeasurementsGate';
import type { ClientPlanItem } from '@/api/generated';
import { formatSignedNumber } from '@/lib/client-metrics';
import IsItWorkingChart from '@/components/client-detail/nutrition/IsItWorkingChart';
import {
  PROGRESS_WEEK_COUNT,
  buildProgress,
  type TargetStatus,
} from '@/components/client-detail/nutrition/nutritionProgress';

const MEASUREMENTS_PAGE_SIZE = 50;

interface Props {
  clientId: string;
  plans: ClientPlanItem[] | undefined;
  /** Target weight from the onboarding answers. */
  targetWeightKg: number | null | undefined;
}

/** "Is it working?" card of the Nutrition tab: weight change over eight weeks against the planned kcal. */
export default function IsItWorkingCard({ clientId, plans, targetWeightKg }: Props) {
  const { t, i18n } = useTranslation();
  const headingId = useId();
  const locale = i18n.language;

  const measurementsGate = useMeasurementsGate();
  const measurementsQuery = useQuery({
    queryKey: ['client-measurements', clientId],
    queryFn: () => getClientMeasurements(clientId, 1, MEASUREMENTS_PAGE_SIZE),
    enabled: Boolean(clientId) && measurementsGate.enabled,
    placeholderData: measurementsGate.placeholderData,
  });

  const items = measurementsQuery.data?.items;
  const model = useMemo(() => buildProgress(plans, items ?? [], targetWeightKg), [plans, items, targetWeightKg]);

  const formatNumber = (value: number) => new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(value);

  const statusText = (status: TargetStatus): { text: string; className: string } | undefined => {
    if (status.kind === 'noTarget' || targetWeightKg == null) {
      return undefined;
    }
    const kg = formatNumber(targetWeightKg);
    switch (status.kind) {
      case 'reached':
        return { text: t('clientDetail.nutrition.isItWorking.reached'), className: 'text-nutrition-ink' };
      case 'onTrack': {
        const sameYear = status.eta.getFullYear() === new Date().getFullYear();
        const month = new Intl.DateTimeFormat(locale, {
          month: 'short',
          ...(sameYear ? {} : { year: 'numeric' as const }),
        }).format(status.eta);
        return { text: t('clientDetail.nutrition.isItWorking.onTrack', { kg, month }), className: 'text-nutrition-ink' };
      }
      case 'farOff':
        return { text: t('clientDetail.nutrition.isItWorking.farOff', { kg }), className: 'text-muted-foreground' };
      default:
        return { text: t('clientDetail.nutrition.isItWorking.offTrack', { kg }), className: 'text-error' };
    }
  };
  const status = model.status ? statusText(model.status) : undefined;

  const chartLabel =
    model.deltaKg !== undefined
      ? t('clientDetail.nutrition.isItWorking.chartLabelDelta', {
          delta: formatSignedNumber(model.deltaKg),
          changes: model.planChanges.length,
        })
      : t('clientDetail.nutrition.isItWorking.chartLabel', { changes: model.planChanges.length });

  const isEmpty = !model.hasWeights && !model.hasBars;

  return (
    <section
      aria-labelledby={headingId}
      className="flex h-full flex-col gap-4 rounded-2xl border border-border bg-card p-5"
    >
      <div className="flex items-center justify-between gap-3">
        <h2 id={headingId} className="text-body font-semibold text-ink">
          {t('clientDetail.nutrition.isItWorking.heading')}
        </h2>
        <span className="text-caption text-muted-foreground">
          {t('clientDetail.nutrition.isItWorking.range', { count: PROGRESS_WEEK_COUNT })}
        </span>
      </div>

      {measurementsQuery.isPending && (
        <div className="flex flex-col gap-3">
          <Skeleton className="h-8 w-40" />
          <Skeleton className="h-40 w-full" />
        </div>
      )}

      {measurementsQuery.isError && (
        <div className="flex flex-col items-start gap-2">
          <p className="text-caption text-muted-foreground">{t('common.dataLoadError')}</p>
          <Button type="button" variant="outline" size="sm" onClick={() => void measurementsQuery.refetch()}>
            {t('clients.retry')}
          </Button>
        </div>
      )}

      {!measurementsQuery.isPending && !measurementsQuery.isError && isEmpty && (
        <p role="status" className="text-body text-muted-foreground">
          {t('clientDetail.nutrition.isItWorking.empty')}
        </p>
      )}

      {!measurementsQuery.isPending && !measurementsQuery.isError && !isEmpty && (
        <>
          <div className="flex flex-wrap items-baseline gap-x-2.5 gap-y-1">
            {model.deltaKg !== undefined ? (
              <>
                <span data-testid="progress-delta" className="font-display text-stat font-semibold text-ink">
                  {t('clientDetail.nutrition.isItWorking.delta', { delta: formatSignedNumber(model.deltaKg) })}
                </span>
                {status && (
                  <span data-testid="progress-status" className={`text-body font-semibold ${status.className}`}>
                    {status.text}
                  </span>
                )}
              </>
            ) : (
              <span data-testid="progress-insufficient" className="text-body text-muted-foreground">
                {t('clientDetail.nutrition.isItWorking.notEnough')}
              </span>
            )}
          </div>

          <IsItWorkingChart model={model} ariaLabel={chartLabel} />

          <div className="mt-auto flex flex-wrap items-center gap-x-4 gap-y-1 text-meta text-muted-foreground">
            <span className="flex items-center gap-1.5">
              <span className="h-0.5 w-3 rounded-xs bg-ink" aria-hidden="true" />
              {t('clientDetail.nutrition.isItWorking.legendWeight')}
            </span>
            <span className="flex items-center gap-1.5">
              <span className="size-2.5 rounded-xs bg-nutrition-soft" aria-hidden="true" />
              {t('clientDetail.nutrition.isItWorking.legendKcal')}
            </span>
          </div>
        </>
      )}
    </section>
  );
}
