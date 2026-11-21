import type { CheckInHistoryItem } from '@/api/checkIns';

/** The three states the history shows; the backend's five collapse onto them. */
export type CheckInStatus = 'sent' | 'reviewed' | 'missed';

export type HistoryEntry =
  | { kind: 'open'; item: CheckInHistoryItem }
  | { kind: 'row'; item: CheckInHistoryItem; status: CheckInStatus | undefined };

export type MonthGroup = {
  key: string;
  /** First day of the month, local time. */
  month: Date;
  rows: Extract<HistoryEntry, { kind: 'row' }>[];
};

/** A date-only "YYYY-MM-DD" as that calendar day in local time; `new Date(iso)` would read it as UTC. */
export function parseCalendarDate(value: string | undefined): Date | null {
  const match = value ? /^(\d{4})-(\d{2})-(\d{2})/.exec(value) : null;
  if (!match) return null;
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const date = new Date(year, month - 1, day);
  const valid =
    date.getFullYear() === year && date.getMonth() === month - 1 && date.getDate() === day;
  return valid ? date : null;
}

function parseInstant(value: string | undefined): number | null {
  const time = value ? Date.parse(value) : Number.NaN;
  return Number.isNaN(time) ? null : time;
}

/** Sorts a check-in into the open-now card, or a history row with its status pill. */
export function classify(item: CheckInHistoryItem, now: number): HistoryEntry {
  switch (item.status) {
    case 'Responded':
      return { kind: 'row', item, status: 'sent' };
    case 'Reviewed':
      return { kind: 'row', item, status: 'reviewed' };
    case 'Expired':
    case 'Dismissed':
      return { kind: 'row', item, status: 'missed' };
    case 'Pending': {
      const due = parseInstant(item.dueAt);
      return due === null || due > now
        ? { kind: 'open', item }
        : { kind: 'row', item, status: 'missed' };
    }
    default:
      return { kind: 'row', item, status: undefined };
  }
}

/** Splits entries into the open check-ins and the month groups, keeping the server order. */
export function buildHistory(
  items: readonly CheckInHistoryItem[],
  now: number,
): { open: CheckInHistoryItem[]; months: MonthGroup[] } {
  const open: CheckInHistoryItem[] = [];
  const months: MonthGroup[] = [];
  const byKey = new Map<string, MonthGroup>();

  for (const item of items) {
    const entry = classify(item, now);
    if (entry.kind === 'open') {
      open.push(item);
      continue;
    }
    const week = parseCalendarDate(item.weekStartDate);
    if (!week) continue;
    const key = `${week.getFullYear()}-${week.getMonth()}`;
    let group = byKey.get(key);
    if (!group) {
      group = { key, month: new Date(week.getFullYear(), week.getMonth(), 1), rows: [] };
      byKey.set(key, group);
      months.push(group);
    }
    group.rows.push(entry);
  }
  return { open, months };
}

export type CoachOption = { id: string; firstName: string; fullName: string };

/** Distinct coaches of the loaded check-ins, in order of first appearance. */
export function coachOptions(items: readonly CheckInHistoryItem[]): CoachOption[] {
  const seen = new Map<string, CoachOption>();
  for (const item of items) {
    const id = item.professionalUserId;
    if (!id || seen.has(id)) continue;
    const fullName = (item.professionalName ?? '').trim();
    seen.set(id, { id, firstName: fullName.split(/\s+/)[0] ?? '', fullName });
  }
  return [...seen.values()];
}

export function splitName(fullName: string | undefined): { firstName: string; lastName: string } {
  const [firstName = '', ...rest] = (fullName ?? '').trim().split(/\s+/);
  return { firstName, lastName: rest.join(' ') };
}

/** "19 Oct" in the active language. */
export function weekLabel(language: string, week: Date): string {
  return new Intl.DateTimeFormat(language, { day: 'numeric', month: 'short' }).format(week);
}

/** "October", with the year once it is not the current one. */
export function monthLabel(language: string, month: Date, now: number): string {
  const sameYear = month.getFullYear() === new Date(now).getFullYear();
  return new Intl.DateTimeFormat(language, {
    month: 'long',
    year: sameYear ? undefined : 'numeric',
  }).format(month);
}

/** Due moment of a check-in in the device time zone, e.g. "Saturday 18:00"; null when the date is missing. */
export function dueMoment(language: string, dueAt: string | undefined): string | null {
  const time = parseInstant(dueAt);
  if (time === null) return null;
  return new Intl.DateTimeFormat(language, {
    weekday: 'long',
    hour: 'numeric',
    minute: '2-digit',
  }).format(time);
}

/** "08:00" as "8:00"; the API time is already in the client time zone and is shown as given. */
export function scheduleTime(sendTime: string | undefined): string {
  return (sendTime ?? '').replace(/^0(?=\d:)/, '');
}

const WEEKDAY_KEYS = [
  'sunday',
  'monday',
  'tuesday',
  'wednesday',
  'thursday',
  'friday',
  'saturday',
] as const;

/** i18n key suffix of an API weekday ("Thursday" -> "thursday"); null for a value the app does not know. */
export function weekdayKey(day: string | undefined): (typeof WEEKDAY_KEYS)[number] | null {
  const key = day?.toLowerCase();
  return WEEKDAY_KEYS.find((candidate) => candidate === key) ?? null;
}

/** Weekday of an instant in the device time zone, e.g. "Saturday"; null when the date is missing. */
export function dueWeekday(language: string, dueAt: string | undefined): string | null {
  const time = parseInstant(dueAt);
  return time === null ? null : new Intl.DateTimeFormat(language, { weekday: 'long' }).format(time);
}
