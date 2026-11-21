import { api } from '@/api/client';
import type { MyCheckInHistoryItem, MyCheckInScheduleItem } from '@/api/generated';

export type CheckInHistoryItem = MyCheckInHistoryItem;

export type CheckInScheduleItem = MyCheckInScheduleItem;

export type CheckInHistoryPage = {
  items: CheckInHistoryItem[];
  page: number;
  /** The endpoint reports no total, so a full page is the only hint that another one exists. */
  hasMore: boolean;
};

export const CHECK_IN_HISTORY_PAGE_SIZE = 50;

export const CHECK_IN_QUERY_KEY = ['check-ins'] as const;

export const CHECK_IN_HISTORY_QUERY_KEY = [...CHECK_IN_QUERY_KEY, 'history'] as const;

export const CHECK_IN_SCHEDULE_QUERY_KEY = [...CHECK_IN_QUERY_KEY, 'schedule'] as const;

const CHECK_IN_REMINDER_TYPE = 'clientcheckinreminder';

/** True for the notification type of a check-in reminder (case-insensitive); its payload carries `responseId`. */
export function isCheckInReminderType(type: unknown): boolean {
  return typeof type === 'string' && type.toLowerCase() === CHECK_IN_REMINDER_TYPE;
}

/** GET /client/weekly-check-ins/schedule: when each coach's check-in arrives, in the client's time zone. */
export async function fetchCheckInSchedule(signal?: AbortSignal): Promise<CheckInScheduleItem[]> {
  const response = await api.getMyCheckInScheduleEndpoint(signal);
  return response.items ?? [];
}

/** One page of GET /client/weekly-check-ins/history. */
export async function fetchCheckInHistoryPage(
  page: number,
  signal?: AbortSignal,
): Promise<CheckInHistoryPage> {
  const response = await api.getMyCheckInHistoryEndpoint(page, CHECK_IN_HISTORY_PAGE_SIZE, signal);
  const items = response.items ?? [];
  return { items, page, hasMore: items.length >= CHECK_IN_HISTORY_PAGE_SIZE };
}
