import { addWeeks, format, startOfISOWeek } from 'date-fns';
import type { TrainerCheckInDto } from '@/api/weekly-checkins';
import { isoWeekNumber } from '@/lib/client-metrics';

/** The four review states the page shows; the backend status maps onto them. */
export type CheckInState = 'notReviewed' | 'missed' | 'waiting' | 'reviewed';

export type CheckInFilter = 'all' | CheckInState;

export const STATE_ORDER: readonly CheckInState[] = ['notReviewed', 'missed', 'waiting', 'reviewed'];

/** Responded → Not reviewed, Expired → Missed, Reviewed → Reviewed; Pending and anything unknown → Waiting. */
export function toCheckInState(status: string | undefined): CheckInState {
  switch (status) {
    case 'Responded':
      return 'notReviewed';
    case 'Expired':
      return 'missed';
    case 'Reviewed':
      return 'reviewed';
    default:
      return 'waiting';
  }
}

export interface CheckInCounts {
  all: number;
  notReviewed: number;
  missed: number;
  waiting: number;
  reviewed: number;
}

export function countCheckIns(rows: readonly TrainerCheckInDto[]): CheckInCounts {
  const counts: CheckInCounts = { all: rows.length, notReviewed: 0, missed: 0, waiting: 0, reviewed: 0 };
  for (const row of rows) {
    counts[toCheckInState(row.status)] += 1;
  }
  return counts;
}

/** Clients inside the plan first, then by name within each group. */
export function sortCheckIns(rows: readonly TrainerCheckInDto[]): TrainerCheckInDto[] {
  return [...rows].sort((a, b) => {
    const outsideA = a.clientIsOutsidePlan === true ? 1 : 0;
    const outsideB = b.clientIsOutsidePlan === true ? 1 : 0;
    if (outsideA !== outsideB) {
      return outsideA - outsideB;
    }
    return (a.clientName ?? '').localeCompare(b.clientName ?? '');
  });
}

/** The Monday of the week that contains `date`, at local noon so DST shifts never move the day. */
export function mondayOf(date: Date): Date {
  const monday = startOfISOWeek(date);
  monday.setHours(12, 0, 0, 0);
  return monday;
}

export function shiftWeek(monday: Date, weeks: number): Date {
  return addWeeks(monday, weeks);
}

/** "YYYY-MM-DD" of a local calendar date; the value sent as `weekStartDate`. */
export function toDateKey(date: Date): string {
  return format(date, 'yyyy-MM-dd');
}

export function weekNumberOf(monday: Date): number {
  return isoWeekNumber(toDateKey(monday)) ?? 0;
}

/** "13–19 Oct", or "28 Sep – 4 Oct" when the week spans two months. */
export function weekRangeLabel(monday: Date, locale: string): string {
  const sunday = new Date(monday);
  sunday.setDate(monday.getDate() + 6);
  const month = new Intl.DateTimeFormat(formatLocale(locale), { month: 'short' });
  if (monday.getMonth() === sunday.getMonth()) {
    return `${monday.getDate()}–${sunday.getDate()} ${month.format(sunday)}`;
  }
  return `${monday.getDate()} ${month.format(monday)} – ${sunday.getDate()} ${month.format(sunday)}`;
}

const HAS_ZONE = /(Z|[+-]\d{2}:?\d{2})$/i;

function parseInstant(iso: string): Date | null {
  const date = new Date(HAS_ZONE.test(iso) ? iso : `${iso}Z`);
  return Number.isNaN(date.getTime()) ? null : date;
}

/** Plain "en" would format US-style ("Oct 16"); the board shows "16 Oct". */
function formatLocale(locale: string): string {
  return locale === 'en' ? 'en-GB' : locale;
}

function formatter(locale: string, timeZone: string | undefined, options: Intl.DateTimeFormatOptions) {
  try {
    return new Intl.DateTimeFormat(formatLocale(locale), { ...options, timeZone });
  } catch {
    // An unknown IANA id must not break the row; fall back to the browser zone.
    return new Intl.DateTimeFormat(formatLocale(locale), options);
  }
}

/** "Thu 16 Oct, 18:00" in the client's time zone. */
export function formatDateTime(iso: string | undefined, timeZone: string | undefined, locale: string): string | null {
  const date = iso ? parseInstant(iso) : null;
  if (!date) {
    return null;
  }
  return formatter(locale, timeZone, {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  }).format(date);
}

/** "Sat 18 Oct" in the client's time zone. */
export function formatDay(iso: string | undefined, timeZone: string | undefined, locale: string): string | null {
  const date = iso ? parseInstant(iso) : null;
  if (!date) {
    return null;
  }
  return formatter(locale, timeZone, { weekday: 'short', day: 'numeric', month: 'short' }).format(date);
}

export function clientInitials(name: string | undefined): string {
  const parts = (name ?? '').trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) {
    return '?';
  }
  const first = parts[0] ?? '';
  const last = parts.length > 1 ? (parts[parts.length - 1] ?? '') : '';
  return `${first.charAt(0)}${last.charAt(0)}`.toUpperCase();
}

export function firstNameOf(name: string | undefined): string {
  return (name ?? '').trim().split(/\s+/)[0] ?? '';
}
