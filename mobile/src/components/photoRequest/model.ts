import type { Collaboration } from '@/api/collaborations';
import { PhotoDiaryMode, PhotoDiaryStatus } from '@/api/generated';
import type { PhotoRequest, PhotoRequestLists } from '@/api/photoRequests';
import type { PhotoMonth, ProgressPhoto } from '@/api/photos';
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

const uploadTime = (photo: ProgressPhoto): number => {
  const time = Date.parse(photo.uploadedAt ?? photo.takenAt ?? '');
  return Number.isNaN(time) ? Number.NEGATIVE_INFINITY : time;
};

/** The photos of one request, oldest upload first. */
export function requestPhotos(months: readonly PhotoMonth[] | undefined, requestId: string): ProgressPhoto[] {
  const wanted = requestId.toLowerCase();
  return (months ?? [])
    .flatMap((month) => month.photos)
    .filter((photo) => photo.diaryRequestId?.toLowerCase() === wanted)
    .sort((a, b) => uploadTime(a) - uploadTime(b));
}

/** Photos of one Send go up within seconds, so a longer pause between two photos starts a new send. */
export const SEND_GAP_MS = 5 * 60 * 1000;

export type PhotoSend = {
  /** When the newest photo of this send went in; null when no photo carries a time. */
  sentAt: Date | null;
  /** The send's photos, oldest first. */
  photos: ProgressPhoto[];
};

/** Splits photos into sends by upload time (a gap over SEND_GAP_MS starts a new one), newest send first. */
export function groupSends(photos: readonly ProgressPhoto[]): PhotoSend[] {
  const ordered = [...photos].sort((a, b) => uploadTime(a) - uploadTime(b));
  const sends: PhotoSend[] = [];
  let previous = Number.NaN;
  for (const photo of ordered) {
    const time = uploadTime(photo);
    const last = sends[sends.length - 1];
    if (last && !(time - previous > SEND_GAP_MS)) last.photos.push(photo);
    else sends.push({ sentAt: null, photos: [photo] });
    previous = time;
  }
  for (const send of sends) {
    const time = uploadTime(send.photos[send.photos.length - 1] as ProgressPhoto);
    send.sentAt = Number.isFinite(time) ? new Date(time) : null;
  }
  return sends.reverse();
}

/**
 * The request day a photo belongs to: the day of its upload, held between 1 and the total like today's day, so a
 * photo sent in the part of the window that spills into the day after the last one counts for the last day.
 */
export function photoDay(request: RequestTiming, photo: ProgressPhoto): number | null {
  const time = uploadTime(photo);
  return Number.isFinite(time) ? requestDay(request, time).day : null;
}

/** True when a photo of this request belongs to today's request day. */
export function sentToday(
  months: readonly PhotoMonth[] | undefined,
  request: RequestTiming & { id: string },
  now: number = Date.now(),
): boolean {
  const today = requestDay(request, now).day;
  return requestPhotos(months, request.id).some((photo) => photoDay(request, photo) === today);
}

/** How one day of an every-day request stands; `unknown` is a past day while the photo list is not available. */
export type DayState = 'sent' | 'missed' | 'today' | 'upcoming' | 'unknown';

export type DayCell = { day: number; date: Date; state: DayState; isToday: boolean };

export type DayGroup = { day: number; date: Date; photos: ProgressPhoto[] };

export type RequestProgress = {
  day: number;
  total: number;
  cells: DayCell[];
  /** Today's photos, oldest first. */
  today: DayGroup;
  /** Past days that have photos, newest day first. */
  earlier: DayGroup[];
  sentDays: number;
  missedDays: number;
  sentToday: boolean;
  /** When the last of today's photos went in. */
  sentAt: Date | null;
  /** False while the photo list is not available: nothing can be said about sent or missed days. */
  known: boolean;
};

/** The local calendar date of day N of a request. */
function requestDayDate(request: RequestTiming, day: number, now: number): Date {
  const created = request.createdAt ? Date.parse(request.createdAt) : Number.NaN;
  const base = new Date(Number.isNaN(created) ? now : created);
  return new Date(base.getFullYear(), base.getMonth(), base.getDate() + day - 1);
}

/**
 * Where an every-day request stands: day N of M, each day's state, today's photos and the earlier days' photos.
 * A past day without a photo is missed; today is sent once a photo's day equals today's day. `photos` are the
 * request's own photos, or undefined while the list could not be loaded.
 */
export function requestProgress(
  request: RequestTiming,
  photos: readonly ProgressPhoto[] | undefined,
  now: number = Date.now(),
): RequestProgress {
  const { day, total } = requestDay(request, now);
  const known = photos !== undefined;
  const byDay = new Map<number, ProgressPhoto[]>();
  for (const photo of photos ?? []) {
    const photoOn = photoDay(request, photo);
    if (photoOn === null) continue;
    byDay.set(photoOn, [...(byDay.get(photoOn) ?? []), photo]);
  }

  const cells: DayCell[] = [];
  for (let index = 1; index <= total; index += 1) {
    const isToday = index === day;
    let state: DayState;
    if (byDay.has(index)) state = 'sent';
    else if (isToday) state = 'today';
    else if (index > day) state = 'upcoming';
    else state = known ? 'missed' : 'unknown';
    cells.push({ day: index, date: requestDayDate(request, index, now), state, isToday });
  }

  const todayPhotos = byDay.get(day) ?? [];
  const last = todayPhotos[todayPhotos.length - 1];
  const sentAt = last ? new Date(uploadTime(last)) : null;
  return {
    day,
    total,
    cells,
    today: { day, date: requestDayDate(request, day, now), photos: todayPhotos },
    earlier: [...byDay.entries()]
      .filter(([index]) => index < day)
      .sort(([a], [b]) => b - a)
      .map(([index, list]) => ({ day: index, date: requestDayDate(request, index, now), photos: list })),
    sentDays: cells.filter((cell) => cell.state === 'sent').length,
    missedDays: cells.filter((cell) => cell.state === 'missed').length,
    sentToday: todayPhotos.length > 0,
    sentAt: sentAt && !Number.isNaN(sentAt.getTime()) ? sentAt : null,
    known,
  };
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
    | '/photo-request/[id]/daily-sent'
    | '/photo-request/[id]/closed'
    | '/photo-request/[id]/sent';
  params: { id: string; count?: string; at?: string };
};

/** The every-day confirmation; the count and send time travel as params because nothing stores them. */
export const dailySentRoute = (id: string, count: number, at: number = Date.now()): PhotoRoute => ({
  pathname: '/photo-request/[id]/daily-sent',
  params: { id, count: String(count), at: String(at) },
});

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
      return { kind: 'running', id: open.id, day, total, sentToday: sentToday(months, open, now) };
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

/** Where tapping a request on Today leads: the intro while pending, else the screen that adds the photos. */
export function statusRoute(status: PhotoStatus): PhotoRoute | null {
  switch (status.kind) {
    case 'pending':
      return introRoute(status.id);
    case 'running':
      return flowRoute(status.id, 'everyDay');
    case 'onceOpen':
      return flowRoute(status.id, 'once');
    case 'onceSent':
      return null;
  }
}

/** One open request on Today's banner list. */
export type BannerRequest =
  | { kind: 'pending'; item: PendingDiaryRequest }
  | { kind: 'running'; request: PhotoRequest; day: number; total: number; sentToday: boolean }
  | { kind: 'onceOpen'; request: PhotoRequest };

/**
 * Every photo request the client can still act on: pending ones, then accepted or in-progress ones (an every-day
 * request until it ends). Completed one-time requests and ended every-day requests are left out.
 */
export function bannerRequests(
  pending: readonly PendingDiaryRequest[] | undefined,
  lists: PhotoRequestLists | undefined,
  months: readonly PhotoMonth[] | undefined,
  now: number = Date.now(),
): BannerRequest[] {
  const waiting = answerablePending(pending, now);
  const pendingIds = new Set(waiting.map((item) => item.requestPublicId?.toLowerCase()));
  const result: BannerRequest[] = waiting.map((item) => ({ kind: 'pending', item }));
  const open = oldestFirst(lists?.open ?? []).filter(
    (request) => isAnswerable(request, now) && !pendingIds.has(request.id.toLowerCase()),
  );
  for (const request of open) {
    if (requestMode(request) === 'everyDay') {
      const { day, total } = requestDay(request, now);
      result.push({ kind: 'running', request, day, total, sentToday: sentToday(months, request, now) });
    } else {
      result.push({ kind: 'onceOpen', request });
    }
  }
  return result;
}
