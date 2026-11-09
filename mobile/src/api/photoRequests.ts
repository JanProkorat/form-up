import { api } from '@/api/client';
import { getErrorStatus, isNetworkError } from '@/api/errors';
import { PhotoDiaryStatus } from '@/api/generated';
import type { ClientPhotoDiaryRequestSummary } from '@/api/generated';
import { PhotoUploadError } from '@/api/photos';

/** A coach's photo request as the client sees it, with the fields every screen relies on present. */
export type PhotoRequest = ClientPhotoDiaryRequestSummary & { id: string };

export const PHOTO_REQUESTS_QUERY_KEY = ['photo-requests'] as const;
export const photoRequestQueryKey = (id: string) => [...PHOTO_REQUESTS_QUERY_KEY, 'one', id] as const;
export const PHOTO_REQUEST_LISTS_QUERY_KEY = [...PHOTO_REQUESTS_QUERY_KEY, 'lists'] as const;

/** Reasons longer than this are refused by the server with a 400. */
export const DISMISS_REASON_MAX_LENGTH = 500;

/** One request per coach is the norm, so one page of each status is plenty. */
const PAGE_SIZE = 50;

const hasId = (request: ClientPhotoDiaryRequestSummary): request is PhotoRequest => Boolean(request.id);

async function fetchByStatus(status: PhotoDiaryStatus, signal?: AbortSignal): Promise<PhotoRequest[]> {
  const response = await api.listClientRequestsEndpoint(1, PAGE_SIZE, status, undefined, signal);
  return (response.items ?? []).filter(hasId);
}

export type PhotoRequestLists = {
  /** Accepted or in progress: the client agreed and has not finished. */
  open: PhotoRequest[];
  completed: PhotoRequest[];
};

/** Accepted, in-progress and completed requests (GET /client/photo-diary-requests, once per status). */
export async function fetchPhotoRequestLists(signal?: AbortSignal): Promise<PhotoRequestLists> {
  const [accepted, inProgress, completed] = await Promise.all([
    fetchByStatus(PhotoDiaryStatus.Accepted, signal),
    fetchByStatus(PhotoDiaryStatus.InProgress, signal),
    fetchByStatus(PhotoDiaryStatus.Completed, signal),
  ]);
  return { open: [...accepted, ...inProgress], completed };
}

/** One request by id (GET /client/photo-diary-requests/{id}); a request that is not the caller's is a 404. */
export async function fetchPhotoRequest(id: string, signal?: AbortSignal): Promise<PhotoRequest> {
  const request = await api.getClientPhotoDiaryRequestEndpoint(id, signal);
  if (!request.id) throw new Error('The photo request has no id.');
  return { ...request, id: request.id };
}

/** Agrees to the request (POST /client/photo-diary-requests/{id}/accept). */
export function acceptPhotoRequest(id: string) {
  return api.acceptRequestEndpoint(id);
}

/** Declines the request, with an optional reason (POST /client/photo-diary-requests/{id}/dismiss). */
export function dismissPhotoRequest(id: string, reason: string | undefined) {
  const trimmed = reason?.trim();
  return api.dismissRequestEndpoint(id, trimmed ? { reason: trimmed } : {});
}

/** Sends the request's photos to the coach (POST /client/photo-diary-requests/{id}/submit). */
export function submitPhotoRequest(id: string) {
  return api.submitRequestEndpoint(id);
}

/** Why one photo did not go through; `closed` means the whole request can no longer be answered. */
export type PhotoUploadFailureKind =
  | 'closed'
  | 'network'
  | 'rejected'
  | 'tooLarge'
  | 'unsupportedType'
  | 'unreadable'
  | 'upload';

/**
 * Maps what a photo upload can throw. A 404 or 409 closes the request; a 400 means the backend refused this
 * photo (or its note); everything else leaves the row to be tried again.
 */
export function classifyPhotoUploadError(error: unknown): PhotoUploadFailureKind {
  if (error instanceof PhotoUploadError) return error.failure;
  if (isNetworkError(error)) return 'network';
  const status = getErrorStatus(error);
  if (status === 404 || status === 409) return 'closed';
  if (status === 400) return 'rejected';
  return 'upload';
}

export type PhotoRequestFailure = 'network' | 'closed' | 'invalidReason' | 'generic';

/**
 * Maps what the accept, dismiss and get calls can throw. A 404 or 409 means the request is gone or no longer
 * open, which the screens show as the closed state; a 400 on dismiss means the reason was refused.
 */
export function classifyPhotoRequestError(error: unknown): PhotoRequestFailure {
  if (isNetworkError(error)) return 'network';
  const status = getErrorStatus(error);
  if (status === 404 || status === 409) return 'closed';
  if (status === 400) return 'invalidReason';
  return 'generic';
}
