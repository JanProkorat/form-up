import type { ClientPlanItem } from '@/api/generated';
import type { NutritionPlanDetail } from '@/api/plan-types';

const MS_PER_DAY = 24 * 60 * 60 * 1000;
const DAYS_PER_WEEK = 7;
/** A gap shorter than a week is rounding between Monday-anchored plans, not a missing plan. */
const MIN_GAP_DAYS = DAYS_PER_WEEK;
const MAX_TIMELINE_MONTHS_BACK = 11;
const MAX_TIMELINE_MONTHS_AHEAD = 12;
const MIN_TIMELINE_MONTHS_AHEAD = 1;

/** A plan status as the plans list reports it. */
export type NutritionPlanStatus = 'Draft' | 'Active' | 'Completed' | 'Archived';

/** Loaded state of one plan's detail fetch, as the cards consume it. */
export interface PlanDetailState {
  plan?: NutritionPlanDetail;
  isPending: boolean;
  isError: boolean;
}

export type WeekSegmentState = 'done' | 'current' | 'upcoming' | 'draft';

/** Calendar date (UTC midnight) from the leading `YYYY-MM-DD` of an ISO string, or undefined. */
export function parseCalendarDate(iso: string | null | undefined): Date | undefined {
  if (!iso) {
    return undefined;
  }
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso);
  if (!match) {
    return undefined;
  }
  return new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3])));
}

/** Today's local calendar date, expressed as UTC midnight so it compares with `parseCalendarDate`. */
export function todayCalendarDate(now: Date = new Date()): Date {
  return new Date(Date.UTC(now.getFullYear(), now.getMonth(), now.getDate()));
}

export function addDays(date: Date, days: number): Date {
  return new Date(date.getTime() + days * MS_PER_DAY);
}

export function diffDays(later: Date, earlier: Date): number {
  return Math.round((later.getTime() - earlier.getTime()) / MS_PER_DAY);
}

/** The next Monday strictly after today, as a local `YYYY-MM-DD`. */
export function nextMondayIso(now: Date = new Date()): string {
  const dayOfWeek = now.getDay();
  const daysUntilMonday = (8 - dayOfWeek) % DAYS_PER_WEEK || DAYS_PER_WEEK;
  const target = new Date(now.getFullYear(), now.getMonth(), now.getDate() + daysUntilMonday);
  const month = String(target.getMonth() + 1).padStart(2, '0');
  const day = String(target.getDate()).padStart(2, '0');
  return `${target.getFullYear()}-${month}-${day}`;
}

export function nutritionPlans(plans: ClientPlanItem[] | undefined): ClientPlanItem[] {
  return (plans ?? []).filter((p) => p.planType === 'Nutrition');
}

/** The Active nutrition plan, else the newest Draft (the list is newest first). */
export function selectCurrentPlan(plans: ClientPlanItem[] | undefined): ClientPlanItem | undefined {
  const nutrition = nutritionPlans(plans);
  return nutrition.find((p) => p.status === 'Active') ?? nutrition.find((p) => p.status === 'Draft');
}

export function selectPastPlans(plans: ClientPlanItem[] | undefined): ClientPlanItem[] {
  return nutritionPlans(plans).filter((p) => p.status === 'Completed' || p.status === 'Archived');
}

/** Active and Draft plans, the ones whose end date needs the plan detail. */
export function selectOpenPlans(plans: ClientPlanItem[] | undefined): ClientPlanItem[] {
  return nutritionPlans(plans).filter((p) => p.status === 'Active' || p.status === 'Draft');
}

/** 1-based week of the plan that `today` falls in; below 1 before the start, above the week count after the end. */
export function currentWeekNumber(startDate: Date | undefined, today: Date): number | undefined {
  if (!startDate) {
    return undefined;
  }
  return Math.floor(diffDays(today, startDate) / DAYS_PER_WEEK) + 1;
}

export interface WeekStripModel {
  segments: { weekNumber: number; state: WeekSegmentState }[];
  /** Current week clamped to 1..weekCount, or undefined when the plan has no start date or has not begun. */
  currentWeek?: number;
  weekCount: number;
  /** First unpublished week after the current one, or undefined when nothing needs publishing. */
  needsPublishingWeek?: number;
  /** Plan has no start date: no "week X of N" and an all-draft strip. */
  isScheduled: boolean;
}

export function buildWeekStrip(plan: NutritionPlanDetail, today: Date): WeekStripModel {
  const weeks = [...plan.weeks].sort((a, b) => a.weekNumber - b.weekNumber);
  const weekCount = weeks.length;
  const startDate = parseCalendarDate(plan.startDate);
  const isScheduled = startDate !== undefined;
  const rawWeek = currentWeekNumber(startDate, today);

  const segments = weeks.map((week) => {
    const published = week.status === 'Published';
    let state: WeekSegmentState;
    if (!isScheduled || rawWeek === undefined) {
      state = 'draft';
    } else if (week.weekNumber < rawWeek) {
      state = published ? 'done' : 'draft';
    } else if (week.weekNumber === rawWeek) {
      state = 'current';
    } else {
      state = published ? 'upcoming' : 'draft';
    }
    return { weekNumber: week.weekNumber, state };
  });

  let needsPublishingWeek: number | undefined;
  if (isScheduled && rawWeek !== undefined && plan.status === 'Active') {
    needsPublishingWeek = weeks.find((w) => w.weekNumber > rawWeek && w.status !== 'Published')?.weekNumber;
  }

  const currentWeek = rawWeek !== undefined && rawWeek >= 1 ? Math.min(rawWeek, weekCount) : undefined;
  return { segments, currentWeek, weekCount, needsPublishingWeek, isScheduled };
}

export interface TimelineBar {
  planId: string;
  name: string;
  status: NutritionPlanStatus;
  start: Date;
  /** Exclusive end. */
  end: Date;
  /** The end date could not be resolved (detail fetch failed), so the bar is a one-week stub. */
  endUnknown: boolean;
  /** Whole weeks the bar spans. */
  weeks: number;
  /** For an Active plan, the current week number; otherwise undefined. */
  currentWeek?: number;
  /** For an Active plan, the plan's week count. */
  weekCount?: number;
}

export type TimelineItem =
  | ({ kind: 'plan' } & TimelineBar)
  | { kind: 'gap'; start: Date; end: Date; days: number };

export interface TimelineModel {
  rangeStart: Date;
  rangeEnd: Date;
  totalDays: number;
  items: TimelineItem[];
  months: { start: Date; leftPct: number }[];
  /** Position of today, as a percentage of the range, or undefined when outside it. */
  todayPct?: number;
  /** Some plan starts before the visible range. */
  hasEarlier: boolean;
  /** Some plan ends after the visible range. */
  hasLater: boolean;
}

function monthStart(date: Date): Date {
  return new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), 1));
}

function addMonths(date: Date, months: number): Date {
  return new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth() + months, 1));
}

/** Resolve plan list rows into bars; plans without a usable start or end are skipped. */
export function buildTimelineBars(
  plans: ClientPlanItem[],
  details: Record<string, PlanDetailState>,
  today: Date,
): TimelineBar[] {
  const bars: TimelineBar[] = [];
  for (const plan of plans) {
    const start = parseCalendarDate(plan.periodStart);
    const status = plan.status as NutritionPlanStatus | undefined;
    if (!plan.planId || !start || !status) {
      continue;
    }
    const base = { planId: plan.planId, name: plan.name ?? '', status, start };
    if (status === 'Completed' || status === 'Archived') {
      const end = parseCalendarDate(plan.periodEnd);
      if (end && end.getTime() > start.getTime()) {
        const adjusted = addDays(end, 1);
        bars.push({
          ...base,
          end: adjusted,
          endUnknown: false,
          weeks: Math.max(1, Math.ceil(diffDays(adjusted, start) / DAYS_PER_WEEK)),
        });
      }
      continue;
    }
    const detail = details[plan.planId];
    const weekCount = detail?.plan?.weeks.length;
    if (weekCount && weekCount > 0) {
      bars.push({
        ...base,
        end: addDays(start, weekCount * DAYS_PER_WEEK),
        endUnknown: false,
        weeks: weekCount,
        currentWeek:
          status === 'Active'
            ? Math.min(Math.max(currentWeekNumber(start, today) ?? 1, 1), weekCount)
            : undefined,
        weekCount: status === 'Active' ? weekCount : undefined,
      });
    } else if (detail?.isError) {
      bars.push({ ...base, end: addDays(start, DAYS_PER_WEEK), endUnknown: true, weeks: 1 });
    }
  }
  return bars;
}

/** Lay bars on a month axis, inserting "no plan" gaps; undefined only when there are no bars at all. */
export function buildTimelineModel(
  bars: TimelineBar[],
  today: Date,
  offsetMonths = 0,
): TimelineModel | undefined {
  if (bars.length === 0) {
    return undefined;
  }
  const sorted = [...bars].sort((a, b) => a.start.getTime() - b.start.getTime());
  const earliestAllowed = addMonths(monthStart(today), -MAX_TIMELINE_MONTHS_BACK);
  // The default window always holds today's month, so plans far ahead cannot push its start past its end.
  const baseStart = monthStart(
    new Date(Math.min(Math.max(sorted[0].start.getTime(), earliestAllowed.getTime()), today.getTime())),
  );
  const latestEnd = sorted.reduce((max, b) => Math.max(max, b.end.getTime()), 0);
  const minEnd = addMonths(monthStart(today), MIN_TIMELINE_MONTHS_AHEAD + 1);
  const maxEnd = addMonths(monthStart(today), MAX_TIMELINE_MONTHS_AHEAD + 1);
  const baseEnd = new Date(
    Math.min(Math.max(addMonths(monthStart(new Date(latestEnd)), 1).getTime(), minEnd.getTime()), maxEnd.getTime()),
  );
  const rangeStart = addMonths(baseStart, offsetMonths);
  const rangeEnd = addMonths(baseEnd, offsetMonths);
  const totalDays = diffDays(rangeEnd, rangeStart);

  // Gaps come from all bars, so a window keeps a gap whose neighbour is off-screen.
  const all: TimelineItem[] = [];
  let cursor: Date | undefined;
  for (const bar of sorted) {
    if (cursor && diffDays(bar.start, cursor) >= MIN_GAP_DAYS) {
      all.push({ kind: 'gap', start: cursor, end: bar.start, days: diffDays(bar.start, cursor) });
    }
    all.push({ kind: 'plan', ...bar });
    if (!cursor || bar.end.getTime() > cursor.getTime()) {
      cursor = bar.end;
    }
  }
  const items = all.filter((i) => i.end.getTime() > rangeStart.getTime() && i.start.getTime() < rangeEnd.getTime());
  if (items.length === 0) {
    // Bars exist but none (and no gap between two) reaches the window.
    items.push({ kind: 'gap', start: rangeStart, end: rangeEnd, days: totalDays });
  }

  const months: TimelineModel['months'] = [];
  for (let m = rangeStart; m.getTime() < rangeEnd.getTime(); m = addMonths(m, 1)) {
    months.push({ start: m, leftPct: (diffDays(m, rangeStart) / totalDays) * 100 });
  }

  const todayOffset = diffDays(today, rangeStart);
  const todayPct = todayOffset >= 0 && todayOffset <= totalDays ? (todayOffset / totalDays) * 100 : undefined;

  const hasEarlier = sorted.some((b) => b.start.getTime() < rangeStart.getTime());
  const hasLater = sorted.some((b) => b.end.getTime() > rangeEnd.getTime());

  return { rangeStart, rangeEnd, totalDays, items, months, todayPct, hasEarlier, hasLater };
}

/** "May – Nov 2026" label for the visible months (the range end is exclusive). */
export function formatTimelineRange(model: TimelineModel, locale: string): string {
  return new Intl.DateTimeFormat(locale, { month: 'short', year: 'numeric', timeZone: 'UTC' }).formatRange(
    model.rangeStart,
    addMonths(model.rangeEnd, -1),
  );
}

/** Left offset and width of a span on the model's axis, clipped to the range, as percentages. */
export function spanPercent(model: TimelineModel, start: Date, end: Date): { leftPct: number; widthPct: number } {
  const clippedStart = Math.max(start.getTime(), model.rangeStart.getTime());
  const clippedEnd = Math.min(end.getTime(), model.rangeEnd.getTime());
  const leftPct = (diffDays(new Date(clippedStart), model.rangeStart) / model.totalDays) * 100;
  const widthPct = (diffDays(new Date(clippedEnd), new Date(clippedStart)) / model.totalDays) * 100;
  return { leftPct, widthPct };
}

/** "1 Sep" style label for a calendar date (UTC midnight). */
export function formatShortDate(date: Date, locale: string): string {
  return new Intl.DateTimeFormat(locale, { day: 'numeric', month: 'short', timeZone: 'UTC' }).format(date);
}

/** Short month name for a calendar date (UTC midnight). */
export function formatMonth(date: Date, locale: string): string {
  return new Intl.DateTimeFormat(locale, { month: 'short', timeZone: 'UTC' }).format(date);
}
