import { useId, useMemo, useState } from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import type { ClientPlanItem } from '@/api/generated';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import {
  buildTimelineBars,
  buildTimelineModel,
  formatDateRange,
  formatMonth,
  formatTimelineRange,
  formatShortDate,
  selectOpenPlans,
  spanPercent,
  todayCalendarDate,
  type PlanDetailState,
  type TimelineBar,
  type TimelineModel,
} from '@/components/client-detail/nutrition/nutritionPlanView';

interface Props {
  clientId: string;
  plans: ClientPlanItem[];
  details: Record<string, PlanDetailState>;
}

/** Months the arrows move the visible range by. */
const TIMELINE_STEP_MONTHS = 3;

const BAR_TONE: Record<string, string> = {
  Active: 'border-nutrition bg-nutrition-soft text-nutrition-ink',
  Draft: 'border-dashed border-nutrition bg-card text-ink',
  Completed: 'border-border bg-card text-ink',
  Archived: 'border-border bg-card text-muted-foreground',
};

function barSubtitle(
  bar: TimelineBar,
  locale: string,
  t: (key: string, options?: Record<string, unknown>) => string,
): string {
  if (bar.endUnknown) {
    return t('common.loadError');
  }
  if (bar.status === 'Draft') {
    return t('clientDetail.nutrition.timeline.starts', { date: formatShortDate(bar.start, locale) });
  }
  const lastDay = new Date(bar.end.getTime() - 1);
  const range = `${formatMonth(bar.start, locale)} – ${formatMonth(lastDay, locale)}`;
  if (bar.status === 'Active' && bar.currentWeek !== undefined && bar.weekCount !== undefined) {
    return t('clientDetail.nutrition.timeline.activeRange', {
      range,
      week: bar.currentWeek,
      total: bar.weekCount,
    });
  }
  return t('clientDetail.nutrition.timeline.range', { range, count: bar.weeks });
}

/** Hover text with the plan's full first and last day, even when the bar is clipped at the range edge. */
function barTooltip(
  bar: TimelineBar,
  locale: string,
  t: (key: string, options?: Record<string, unknown>) => string,
): string {
  if (bar.endUnknown) {
    return `${bar.name} · ${barSubtitle(bar, locale, t)}`;
  }
  const lastDay = new Date(bar.end.getTime() - 1);
  return `${bar.name} · ${formatDateRange(bar.start, lastDay, locale)}`;
}

function TimelineRow({ model, locale, clientId }: { model: TimelineModel; locale: string; clientId: string }) {
  const { t } = useTranslation();

  return (
    <div className="relative h-14" data-testid="plan-timeline-row">
      {model.items.map((item) => {
        const { leftPct, widthPct } = spanPercent(model, item.start, item.end);
        const style = { left: `${leftPct}%`, width: `${widthPct}%` };
        if (item.kind === 'gap') {
          const label = t('clientDetail.nutrition.timeline.noPlan');
          const range = `${formatShortDate(item.start, locale)} – ${formatShortDate(new Date(item.end.getTime() - 1), locale)}`;
          return (
            <div
              key={`gap-${item.start.getTime()}`}
              style={style}
              title={`${label} · ${range}`}
              className="absolute top-0 box-border h-full overflow-hidden px-0.5"
            >
              <div className="flex h-full flex-col justify-center rounded-lg border border-dashed border-border px-2.5">
                <span className="truncate text-body font-semibold text-muted-foreground">{label}</span>
                <span className="truncate text-caption text-muted-foreground">{range}</span>
              </div>
            </div>
          );
        }
        return (
          <div
            key={item.planId}
            style={style}
            className="absolute top-0 box-border h-full px-0.5"
          >
            <Link
              to={`/clients/${clientId}/nutrition/plans/${item.planId}`}
              aria-label={`${item.name} · ${barSubtitle(item, locale, t)}`}
              title={barTooltip(item, locale, t)}
              className={cn(
                'flex h-full w-full flex-col justify-center overflow-hidden rounded-lg border px-2.5 text-left outline-none focus-visible:ring-3 focus-visible:ring-ring/50',
                BAR_TONE[item.status] ?? BAR_TONE.Completed,
              )}
            >
              <span className="truncate text-body font-semibold">{item.name}</span>
              <span className="truncate text-caption text-muted-foreground">{barSubtitle(item, locale, t)}</span>
            </Link>
          </div>
        );
      })}
    </div>
  );
}

/**
 * "Plan timeline" card: the client's nutrition plans laid on a month axis, with
 * "No plan" gaps and a today marker. Active and Draft plans take their length
 * from the plan detail; Completed and Archived ones from the list's period end.
 * A bar links to the client plan editor.
 */
export default function PlanTimelineCard({ clientId, plans, details }: Props) {
  const { t, i18n } = useTranslation();
  const headingId = useId();
  const locale = i18n.language;
  const [offset, setOffset] = useState(0);

  const model = useMemo(() => {
    const today = todayCalendarDate();
    const bars = buildTimelineBars(
      plans.filter((p) => p.planType === 'Nutrition' && p.periodStart),
      details,
      today,
    );
    return buildTimelineModel(bars, today, offset);
  }, [plans, details, offset]);

  const detailsPending = selectOpenPlans(plans).some((p) => p.planId && details[p.planId]?.isPending);

  return (
    <section
      aria-labelledby={headingId}
      className="flex flex-col gap-4 rounded-2xl border border-border bg-card p-5"
    >
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <h2 id={headingId} className="text-card-title font-bold text-ink">
          {t('clientDetail.nutrition.timeline.heading')}
        </h2>
        {model && (
          <>
            <span className="inline-flex items-center gap-1.5">
              <Button
                type="button"
                variant="outline"
                size="icon"
                aria-label={t('clientDetail.nutrition.timeline.earlier')}
                title={t('clientDetail.nutrition.timeline.earlier')}
                disabled={!model.hasEarlier}
                onClick={() => setOffset((o) => o - TIMELINE_STEP_MONTHS)}
              >
                <ChevronLeft aria-hidden="true" />
              </Button>
              <Button
                type="button"
                variant="outline"
                size="icon"
                aria-label={t('clientDetail.nutrition.timeline.later')}
                title={t('clientDetail.nutrition.timeline.later')}
                disabled={!model.hasLater}
                onClick={() => setOffset((o) => o + TIMELINE_STEP_MONTHS)}
              >
                <ChevronRight aria-hidden="true" />
              </Button>
            </span>
            <Button type="button" variant="outline" className="px-3 font-semibold" onClick={() => setOffset(0)}>
              {t('clientDetail.nutrition.timeline.todayAction')}
            </Button>
            <span className="text-body font-semibold text-ink" data-testid="plan-timeline-range">
              {formatTimelineRange(model, locale)}
            </span>
          </>
        )}
        <span className="ml-auto text-meta text-muted-foreground">{t('clientDetail.nutrition.timeline.hint')}</span>
      </div>

      {!model && (
        <p className="text-caption text-muted-foreground">
          {detailsPending ? t('common.loading') : t('clientDetail.nutrition.timeline.empty')}
        </p>
      )}

      {model && (
        <div className="overflow-x-auto">
          <div className="relative min-w-160 pt-6 pb-1">
            {model.todayPct !== undefined && (
              <div
                aria-hidden="true"
                data-testid="plan-timeline-today-marker"
                className="absolute top-0 flex flex-col items-center"
                style={{ left: `${model.todayPct}%` }}
              >
                <span className="-translate-x-1/2 text-label font-bold uppercase tracking-wider text-error">
                  {t('clientDetail.nutrition.timeline.today')}
                </span>
                <span className="h-2 w-0.5 bg-error" />
              </div>
            )}
            <TimelineRow model={model} locale={locale} clientId={clientId} />
            <div className="relative mt-2 h-5 border-t border-border">
              {model.todayPct !== undefined && (
                <span
                  aria-hidden="true"
                  className="absolute -top-2.5 h-2 w-0.5 bg-error"
                  style={{ left: `${model.todayPct}%` }}
                />
              )}
              {model.months.map((month) => (
                <span
                  key={month.start.getTime()}
                  className="absolute top-1 border-l border-border pl-1.5 text-body text-muted-foreground"
                  style={{ left: `${month.leftPct}%` }}
                >
                  {formatMonth(month.start, locale)}
                </span>
              ))}
            </div>
          </div>
        </div>
      )}
    </section>
  );
}
