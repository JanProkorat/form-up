import type { TFunction } from 'i18next';

import type { RequestCoach } from './hooks';
import type { PendingDiaryRequest } from '@/api/questionnaires';
import { asCoachRole } from './model';
import type { RequestMode } from './model';

/** "Mon 12 Oct" in the app language. */
export function formatRequestDay(language: string, value: Date | string | undefined): string {
  const date = value instanceof Date ? value : new Date(value ?? Number.NaN);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleDateString(language, { weekday: 'short', day: 'numeric', month: 'short' });
}

/** "08:12" in the app language. */
export function formatClock(language: string, value: Date): string {
  return value.toLocaleTimeString(language, { hour: 'numeric', minute: '2-digit' });
}

/** One-letter weekday ("W") for the day strip. */
export const dayLetter = (language: string, value: Date): string =>
  value.toLocaleDateString(language, { weekday: 'narrow' });

/** "7 days" with the language's plural form. */
export const daysLabel = (t: TFunction, count: number): string => t('photoRequest.days', { count });

/** How the coach is named in a sentence: the first name, else the role ("Your trainer"). */
export function coachLabel(t: TFunction, coach: Pick<RequestCoach, 'first' | 'role'> | null): string {
  return coach?.first || t(`photoRequest.coachFallback.${coach?.role ?? 'other'}`);
}

/** The coach of a pending banner item, named the same way. */
export function pendingCoachLabel(t: TFunction, item: PendingDiaryRequest): string {
  const first = (item.professionalName ?? '').trim().split(/\s+/)[0] ?? '';
  return coachLabel(t, { first, role: asCoachRole(item.professionalRole) });
}

export const modeLabel = (t: TFunction, mode: RequestMode | null): string =>
  t(mode === 'everyDay' ? 'photoRequest.rows.everyDay' : 'photoRequest.rows.once');
