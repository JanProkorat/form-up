import type { Collaboration } from '@/api/collaborations';
import { PhotoDiaryMode, PhotoDiaryStatus } from '@/api/generated';
import type { PhotoRequest, PhotoRequestLists } from '@/api/photoRequests';
import type { PhotoMonth } from '@/api/photos';
import type { PendingDiaryRequest } from '@/api/questionnaires';
import type { CoachRole } from '@/components/today/collaborationRoles';

export type RequestMode = 'everyDay' | 'once';

/** The fields of a request that decide its schedule; both the pending banner item and the full request carry them. */
export type RequestTiming = {
  mode?: PhotoDiaryMode | undefined;
  createdAt?: string | undefined;
  durationDays?: number | undefined;
};

const DAY_MS = 24 * 60 * 60 * 1000;

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** A route param that is a single, well-formed id; anything else is treated as missing. */
export const readRequestId = (value: string | string[] | undefined): string => {
  const single = Array.isArray(value) ? value[0] : value;
  return single !== undefined && UUID.test(single) ? single : '';
};

/** Every day (Workflow) or once (Bulk); a legacy request has no mode. */
export function requestMode(request: RequestTiming): RequestMode | null {
  if (request.mode === PhotoDiaryMode.Workflow) return 'everyDay';
  if (request.mode === PhotoDiaryMode.Bulk) return 'once';
  return null;
}

/** Days the request stays open; only an every-day request has an end. */
export const requestDays = (request: RequestTiming): number => Math.max(request.durationDays ?? 0, 1);

/** End of an every-day request (CreatedAt + DurationDays); a one-time request never ends. */
export function requestEndsAt(request: RequestTiming): Date | null {
  if (requestMode(request) !== 'everyDay') return null;
  const created = request.createdAt ? Date.parse(request.createdAt) : Number.NaN;
  if (Number.isNaN(created)) return null;
  return new Date(created + requestDays(request) * DAY_MS);
}

/** True once an every-day request has reached its end. */
export function hasEnded(request: RequestTiming, now: number = Date.now()): boolean {
  const end = requestEndsAt(request);
  return end !== null && end.getTime() <= now;
}

/** Midnight of a device-local calendar day, expressed as a UTC day count so DST never skews a difference. */
const localDayNumber = (time: number): number => {
  const date = new Date(time);
  return Math.floor(Date.UTC(date.getFullYear(), date.getMonth(), date.getDate()) / DAY_MS);
};

/** Day N of an every-day request in the device's local days, held between 1 and the total. */
export function requestDay(request: RequestTiming, now: number = Date.now()): { day: number; total: number } {
  const total = requestDays(request);
  const created = request.createdAt ? Date.parse(request.createdAt) : Number.NaN;
  if (Number.isNaN(created)) return { day: 1, total };
  const day = localDayNumber(now) - localDayNumber(created) + 1;
  return { day: Math.min(Math.max(day, 1), total), total };
}

/** True when a photo of this request was taken on the device's current calendar day. */
export function sentToday(
  months: readonly PhotoMonth[] | undefined,
  requestId: string,
  now: number = Date.now(),
): boolean {
  const today = localDayNumber(now);
  const wanted = requestId.toLowerCase();
  return (months ?? []).some((month) =>
    month.photos.some((photo) => {
      if (photo.diaryRequestId?.toLowerCase() !== wanted) return false;
      const taken = Date.parse(photo.takenAt ?? photo.uploadedAt ?? '');
      return !Number.isNaN(taken) && localDayNumber(taken) === today;
    }),
  );
}

/** Pending, accepted or in progress: the client can still act on it. */
export const isOpenStatus = (status: PhotoDiaryStatus | undefined): boolean =>
  status === PhotoDiaryStatus.Pending ||
  status === PhotoDiaryStatus.Accepted ||
  status === PhotoDiaryStatus.InProgress;

/** Can the client still answer this request? Closed ones go to the closed screen. */
export const isAnswerable = (request: PhotoRequest, now: number = Date.now()): boolean =>
  isOpenStatus(request.status) && requestMode(request) !== null && !hasEnded(request, now);

export type PhotoRoute = {
  pathname:
    | '/photo-request/[id]'
    | '/photo-request/[id]/once'
    | '/photo-request/[id]/daily'
    | '/photo-request/[id]/closed'
    | '/photo-request/[id]/sent';
  params: { id: string; count?: string; at?: string };
};

export const introRoute = (id: string): PhotoRoute => ({ pathname: '/photo-request/[id]', params: { id } });
export const closedRoute = (id: string): PhotoRoute => ({ pathname: '/photo-request/[id]/closed', params: { id } });

/** The one-time request's confirmation; the count and send time travel as params because nothing stores them. */
export const sentRoute = (id: string, count: number, at: number = Date.now()): PhotoRoute => ({
  pathname: '/photo-request/[id]/sent',
  params: { id, count: String(count), at: String(at) },
});

/** A positive whole number from a route param; anything else is missing. */
export function readPositiveInt(value: string | string[] | undefined): number | null {
  const single = Array.isArray(value) ? value[0] : value;
  const parsed = single !== undefined && /^\d+$/.test(single) ? Number(single) : Number.NaN;
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null;
}

/** Where adding photos happens once the request is accepted. */
export const flowRoute = (id: string, mode: RequestMode | null): PhotoRoute => ({
  pathname: mode === 'everyDay' ? '/photo-request/[id]/daily' : '/photo-request/[id]/once',
  params: { id },
});

const normalize = (name: string | undefined): string => (name ?? '').trim().toLowerCase();

export const asCoachRole = (value: string | undefined): CoachRole | undefined =>
  value === 'Trainer' || value === 'Nutritionist' ? value : undefined;

const createdTime = (request: RequestTiming): number => {
  const time = request.createdAt ? Date.parse(request.createdAt) : Number.NaN;
  return Number.isNaN(time) ? Number.POSITIVE_INFINITY : time;
};

const oldestFirst = <T extends RequestTiming>(items: readonly T[]): T[] =>
  [...items].sort((a, b) => createdTime(a) - createdTime(b));

/** Pending requests the client can still answer, oldest first. */
export const answerablePending = (
  items: readonly PendingDiaryRequest[] | undefined,
  now: number = Date.now(),
): PendingDiaryRequest[] =>
  oldestFirst((items ?? []).filter((item) => Boolean(item.requestPublicId) && !hasEnded(item, now)));

const sameId = (a: string | undefined, b: string | undefined): boolean =>
  a !== undefined && b !== undefined && a.toLowerCase() === b.toLowerCase();

/**
 * Whether a non-pending request belongs to this coach. A request carries the coach's profile id as
 * `professionalPublicId`, the same id a collaboration has. Without it (an older response) the request only has the
 * coach's user id, which matches a collaboration only if the backend ever aligns them, so a lone collaboration
 * owns every request.
 */
export const belongsToCollaboration = (
  request: PhotoRequest,
  collaboration: Collaboration,
  onlyCollaboration: boolean,
): boolean => {
  if (request.professionalPublicId) return sameId(request.professionalPublicId, collaboration.professionalPublicId);
  return onlyCollaboration || request.professionalId === collaboration.professionalPublicId;
};

/** The oldest pending request from this coach; an unmatched one belongs to the only coach. */
export function pendingFor(
  items: readonly PendingDiaryRequest[],
  collaboration: Collaboration,
  onlyCollaboration: boolean,
): PendingDiaryRequest | undefined {
  const name = normalize(collaboration.professionalName);
  const named = items.filter((item) => normalize(item.professionalName) === name);
  return (named.length > 0 ? named : onlyCollaboration ? items : [])[0];
}

/** What one coach's photo request looks like on Today. */
export type PhotoStatus =
  | { kind: 'pending'; id: string; mode: RequestMode | null; days: number; endsAt: Date | null }
  | { kind: 'running'; id: string; day: number; total: number; sentToday: boolean }
  | { kind: 'onceOpen'; id: string }
  | { kind: 'onceSent'; id: string };

type StatusInput = {
  collaboration: Collaboration;
  onlyCollaboration: boolean;
  pending: readonly PendingDiaryRequest[] | undefined;
  lists: PhotoRequestLists | undefined;
  months: readonly PhotoMonth[] | undefined;
  now?: number;
};

/** The request that matters for this coach: one to act on, then a new one, then a finished one-time request. */
export function photoStatusFor({
  collaboration,
  onlyCollaboration,
  pending,
  lists,
  months,
  now = Date.now(),
}: StatusInput): PhotoStatus | null {
  const mine = (request: PhotoRequest) => belongsToCollaboration(request, collaboration, onlyCollaboration);

  const open = oldestFirst((lists?.open ?? []).filter(mine)).find((request) => isAnswerable(request, now));
  if (open) {
    if (requestMode(open) === 'everyDay') {
      const { day, total } = requestDay(open, now);
      return { kind: 'running', id: open.id, day, total, sentToday: sentToday(months, open.id, now) };
    }
    return { kind: 'onceOpen', id: open.id };
  }

  const waiting = pendingFor(answerablePending(pending, now), collaboration, onlyCollaboration);
  if (waiting?.requestPublicId) {
    return {
      kind: 'pending',
      id: waiting.requestPublicId,
      mode: requestMode(waiting),
      days: requestDays(waiting),
      endsAt: requestEndsAt(waiting),
    };
  }

  const sent = oldestFirst((lists?.completed ?? []).filter(mine))
    .reverse()
    .find((request) => requestMode(request) === 'once');
  return sent ? { kind: 'onceSent', id: sent.id } : null;
}
