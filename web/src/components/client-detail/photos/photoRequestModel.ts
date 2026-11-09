import { useTranslation } from 'react-i18next';
import type { ClientPlanItem } from '@/api/client-plans';
import { PhotoDiaryMode } from '@/api/generated';
import type { ClientPhotoResponse } from '@/api/generated';
import type { PhotoDiaryRequestSummary } from '@/api/diary-requests';

export const REQUEST_MIN_DAYS = 1;
export const REQUEST_MAX_DAYS = 30;
export const REQUEST_DEFAULT_DAYS = 7;
export const REQUEST_MESSAGE_MAX = 500;
/** Page size of the open-request lookup: the newest few are enough to find the open one. */
export const OPEN_REQUEST_PAGE_SIZE = 10;

export const DAY_OPTIONS = Array.from(
  { length: REQUEST_MAX_DAYS - REQUEST_MIN_DAYS + 1 },
  (_, index) => REQUEST_MIN_DAYS + index,
);

export type RequestFrequency = 'once' | 'daily';

const DAY_MS = 24 * 60 * 60 * 1000;

function parse(iso: string | undefined): Date | undefined {
  if (!iso) {
    return undefined;
  }
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? undefined : date;
}

/** The form preview's end of the window: now + days, the server's formula. A one-time request has none. */
export function previewEndsAt(frequency: RequestFrequency, days: number, now: Date = new Date()): Date | undefined {
  return frequency === 'once' ? undefined : new Date(now.getTime() + days * DAY_MS);
}

/** "Tue 20 Oct" in the coach's local time. */
export function formatWeekdayDate(value: Date | string | undefined, locale: string): string {
  const date = value instanceof Date ? value : parse(value);
  return date
    ? new Intl.DateTimeFormat(locale, { weekday: 'short', day: 'numeric', month: 'short' }).format(date)
    : '';
}

/** "9 Oct" in the coach's local time. */
export function formatDayMonth(iso: string | undefined, locale: string): string {
  const date = parse(iso);
  return date ? new Intl.DateTimeFormat(locale, { day: 'numeric', month: 'short' }).format(date) : '';
}

/** The newest request that is still open; the list comes back newest first. */
export function findOpenRequest(items: PhotoDiaryRequestSummary[] | undefined): PhotoDiaryRequestSummary | undefined {
  return items?.find((item) => item.isOpen === true);
}

/** True when a loaded photo was uploaded against this request. */
export function hasPhotoForRequest(photos: ClientPhotoResponse[], requestId: string | undefined): boolean {
  return requestId !== undefined && photos.some((photo) => photo.diaryRequestId === requestId);
}

export function planKind(plan: ClientPlanItem): 'nutrition' | 'training' {
  return plan.planType === 'Nutrition' ? 'nutrition' : 'training';
}

export function findPlan(plans: ClientPlanItem[] | undefined, planId: string | undefined): ClientPlanItem | undefined {
  return planId ? plans?.find((plan) => plan.planId === planId) : undefined;
}

/** Once (Bulk) or every day (Workflow); a request without a stored mode is read from its length. */
export function requestFrequency(request: PhotoDiaryRequestSummary): RequestFrequency {
  if (request.mode === PhotoDiaryMode.Bulk) {
    return 'once';
  }
  if (request.mode === PhotoDiaryMode.Workflow) {
    return 'daily';
  }
  return (request.durationDays ?? REQUEST_DEFAULT_DAYS) <= 1 ? 'once' : 'daily';
}

/** The plan name with its type, as the picker and the status view show it. */
export function usePlanLabel() {
  const { t } = useTranslation();
  return (plan: ClientPlanItem) =>
    t('clientDetail.photos.requestDrawer.planOption', {
      name: plan.name ?? '',
      type: t(`clientDetail.photos.requestDrawer.planType.${planKind(plan)}`),
    });
}
