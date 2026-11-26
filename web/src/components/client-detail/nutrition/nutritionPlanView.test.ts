import { describe, expect, it } from 'vitest';
import {
  buildTimelineModel,
  formatTimelineRange,
  spanPercent,
  type TimelineBar,
} from '@/components/client-detail/nutrition/nutritionPlanView';

const TODAY = new Date(Date.UTC(2026, 5, 10));

function d(iso: string): Date {
  return new Date(`${iso}T00:00:00Z`);
}

function bar(planId: string, start: string, end: string, overrides: Partial<TimelineBar> = {}): TimelineBar {
  return {
    planId,
    name: planId,
    status: 'Completed',
    start: d(start),
    end: d(end),
    endUnknown: false,
    weeks: 4,
    ...overrides,
  };
}

/** Range label with thin / no-break spaces folded, since ICU versions differ. */
function label(model: NonNullable<ReturnType<typeof buildTimelineModel>>): string {
  return formatTimelineRange(model, 'en').replace(/\s/g, ' ');
}

function iso(date: Date): string {
  return date.toISOString().slice(0, 10);
}

describe('buildTimelineModel', () => {
  const jan = bar('jan', '2026-01-05', '2026-02-02');

  it('returns undefined only when there are no bars', () => {
    expect(buildTimelineModel([], TODAY)).toBeUndefined();
  });

  it('keeps the default range at offset 0 and shifts both ends by whole months', () => {
    const base = buildTimelineModel([jan], TODAY)!;
    expect([iso(base.rangeStart), iso(base.rangeEnd)]).toEqual(['2026-01-01', '2026-08-01']);

    const shifted = buildTimelineModel([jan], TODAY, -3)!;
    expect([iso(shifted.rangeStart), iso(shifted.rangeEnd)]).toEqual(['2025-10-01', '2026-05-01']);
    expect(shifted.months).toHaveLength(base.months.length);
    expect(shifted.months[0].leftPct).toBe(0);
  });

  it('reports whether plans lie before or after the range', () => {
    const base = buildTimelineModel([jan], TODAY)!;
    expect(base.hasEarlier).toBe(false);
    expect(base.hasLater).toBe(false);

    const later = buildTimelineModel([jan], TODAY, 3)!;
    expect(later.hasEarlier).toBe(true);
    expect(later.hasLater).toBe(false);

    const earlier = buildTimelineModel([jan], TODAY, -8)!;
    expect(earlier.hasEarlier).toBe(false);
    expect(earlier.hasLater).toBe(true);
  });

  it('still returns a model with a full-width gap when every plan is older than the range', () => {
    const old = bar('old', '2024-01-01', '2024-03-01');
    const model = buildTimelineModel([old], TODAY)!;

    expect(model.hasEarlier).toBe(true);
    expect(model.hasLater).toBe(false);
    expect(model.items).toHaveLength(1);
    expect(model.items[0].kind).toBe('gap');
    expect(spanPercent(model, model.items[0].start, model.items[0].end)).toEqual({ leftPct: 0, widthPct: 100 });
  });

  it('spans one gap across a window between two distant plans and hides the today marker', () => {
    const a = bar('a', '2023-01-02', '2023-03-01');
    const b = bar('b', '2028-01-03', '2028-02-01');
    const model = buildTimelineModel([a, b], TODAY, -14)!;

    expect(model.items.filter((i) => i.kind === 'plan')).toHaveLength(0);
    const gaps = model.items.filter((i) => i.kind === 'gap');
    expect(gaps).toHaveLength(1);
    expect(spanPercent(model, gaps[0].start, gaps[0].end)).toEqual({ leftPct: 0, widthPct: 100 });
    expect(model.todayPct).toBeUndefined();
  });

  it('shows the today marker only while today is inside the range', () => {
    expect(buildTimelineModel([jan], TODAY)!.todayPct).toBeGreaterThan(0);
    expect(buildTimelineModel([jan], TODAY, -8)!.todayPct).toBeUndefined();
  });

  it('clips a bar visually but keeps its full start and end', () => {
    const old = bar('old', '2024-11-04', '2025-01-06');
    const model = buildTimelineModel([old, jan], TODAY, -6)!;
    const item = model.items.find((i) => i.kind === 'plan' && i.planId === 'old');

    expect(item).toBeDefined();
    expect(iso(item!.start)).toBe('2024-11-04');
    expect(iso(item!.end)).toBe('2025-01-06');
    const { leftPct, widthPct } = spanPercent(model, item!.start, item!.end);
    expect(leftPct).toBe(0);
    expect(widthPct).toBeGreaterThan(0);
    expect(widthPct).toBeLessThan(10);
  });

  it('positions and clips a one-week stub whose detail failed to load', () => {
    const stub = bar('stub', '2026-05-28', '2026-06-04', {
      status: 'Active',
      endUnknown: true,
      weeks: 1,
    });
    const model = buildTimelineModel([stub], TODAY, 1)!;
    const item = model.items.find((i) => i.kind === 'plan')!;

    expect(model.rangeStart.getTime()).toBeGreaterThan(stub.start.getTime());
    expect(item.kind === 'plan' && item.endUnknown).toBe(true);
    const { leftPct, widthPct } = spanPercent(model, item.start, item.end);
    expect(leftPct).toBe(0);
    expect(widthPct).toBeCloseTo((3 / model.totalDays) * 100);
  });
});

describe('formatTimelineRange', () => {
  const jan = bar('jan', '2026-01-05', '2026-02-02');

  it('names the first and last included month', () => {
    expect(label(buildTimelineModel([jan], TODAY)!)).toBe('Jan – Jul 2026');
  });

  it('shows both years when the range crosses a year', () => {
    expect(label(buildTimelineModel([jan], TODAY, -3)!)).toBe('Oct 2025 – Apr 2026');
  });
});
