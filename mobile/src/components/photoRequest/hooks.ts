import { useLocalSearchParams, useRouter } from 'expo-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import type { Collaboration } from '@/api/collaborations';
import {
  PHOTO_REQUESTS_QUERY_KEY,
  PHOTO_REQUEST_LISTS_QUERY_KEY,
  acceptPhotoRequest,
  dismissPhotoRequest,
  fetchPhotoRequest,
  fetchPhotoRequestLists,
  photoRequestQueryKey,
  submitPhotoRequest,
} from '@/api/photoRequests';
import type { PhotoRequest } from '@/api/photoRequests';
import { PHOTOS_QUERY_KEY, uploadRequestPhoto } from '@/api/photos';
import type { PickedPhoto, RequestPhotoTarget } from '@/api/photos';
import { PENDING_QUESTIONNAIRES_QUERY_KEY } from '@/api/questionnaires';
import { getErrorStatus } from '@/api/errors';
import { parseCoachRoles } from '@/components/invites/format';
import type { CoachRole } from '@/components/today/collaborationRoles';
import { useCollaborations } from '@/hooks/useCollaborations';
import { usePendingQuestionnaires } from '@/hooks/usePendingQuestionnaires';
import { usePhotos } from '@/hooks/usePhotos';
import { useAuthStore } from '@/stores/auth';

import { asCoachRole, photoStatusFor, readRequestId, requestPhotos, requestProgress } from './model';
import type { PhotoStatus } from './model';

/** The request id of the current route; empty when the param is missing or not a well-formed id. */
export function useRequestId(): string {
  const params = useLocalSearchParams<{ id?: string | string[] }>();
  return readRequestId(params.id);
}

/** Leaves the request screens: back to where the client came from, or Today when opened from a push or link. */
export function useLeaveRequest(): () => void {
  const router = useRouter();
  return () => {
    if (router.canGoBack()) router.back();
    else router.replace('/');
  };
}

/** Goes back to Today from deeper in the flow, dropping the request screens in between. */
export function useBackToToday(): () => void {
  const router = useRouter();
  return () => router.dismissTo('/');
}

/** One request by id. A 404 is final (stale push or link), so it is not retried. */
export function usePhotoRequest(id: string) {
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  return useQuery({
    queryKey: photoRequestQueryKey(id),
    queryFn: ({ signal }) => fetchPhotoRequest(id, signal),
    enabled: isAuthenticated && id !== '',
    retry: (count, error) => getErrorStatus(error) !== 404 && count < 1,
  });
}

/** Accepted, in-progress and completed requests; a failure leaves the data undefined. */
export function usePhotoRequestLists() {
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  return useQuery({
    queryKey: PHOTO_REQUEST_LISTS_QUERY_KEY,
    queryFn: ({ signal }) => fetchPhotoRequestLists(signal),
    enabled: isAuthenticated,
    retry: false,
  });
}

/** What this coach's photo request looks like on Today; null when there is nothing to show. */
export function usePhotoStatus(collaboration: Collaboration | undefined, onlyCollaboration: boolean): PhotoStatus | null {
  const { data: pending } = usePendingQuestionnaires();
  const { data: lists } = usePhotoRequestLists();
  const { data: months } = usePhotos();
  if (!collaboration) return null;
  return photoStatusFor({
    collaboration,
    onlyCollaboration,
    pending: pending?.diaryRequests,
    lists,
    months,
  });
}

/**
 * Where an every-day request stands, from the client's photo list. `photos` is the query behind it: while it is
 * pending or failed the progress has no sent or missed days, and the screens show that state themselves.
 */
export function useRequestProgress(request: PhotoRequest | undefined, options?: { fresh?: boolean }) {
  const photos = usePhotos(options);
  const progress = request
    ? requestProgress(request, photos.data ? requestPhotos(photos.data, request.id) : undefined)
    : null;
  return { progress, photos };
}

export type RequestCoach ={ name: string; first: string; role: CoachRole | undefined };

/**
 * The coach behind a request: the name and role the request itself carries. A request without them (the role is
 * null for one sent with an invite) falls back to the matching collaboration and the pending list, and an older
 * response without a coach profile id is matched by user id, then by the pending list's name, then as the only
 * collaboration the client has.
 */
export function useRequestCoach(request: PhotoRequest | undefined): RequestCoach | null {
  const { data: collaborations } = useCollaborations();
  const { data: pending } = usePendingQuestionnaires();
  if (!request) return null;

  const fromPending = pending?.diaryRequests.find((item) => item.requestPublicId === request.id);
  const list = collaborations ?? [];
  const publicId = request.professionalPublicId?.toLowerCase();
  const collaboration = publicId
    ? list.find((entry) => entry.professionalPublicId?.toLowerCase() === publicId)
    : (list.find((entry) => entry.professionalPublicId === request.professionalId) ??
      (fromPending ? list.find((entry) => entry.professionalName === fromPending.professionalName) : undefined) ??
      (list.length === 1 ? list[0] : undefined));

  const name = (
    request.professionalName ??
    collaboration?.professionalName ??
    fromPending?.professionalName ??
    ''
  ).trim();
  const role =
    asCoachRole(request.professionalRole) ??
    asCoachRole(fromPending?.professionalRole) ??
    parseCoachRoles(collaboration?.role)[0];
  if (name === '' && role === undefined) return null;
  return { name, first: name.split(/\s+/)[0] ?? '', role };
}

/** Refreshes everything a changed request affects: Today's banner and steps, and the request itself. */
export function useRefreshPhotoRequests() {
  const queryClient = useQueryClient();
  return async (id?: string) => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: PENDING_QUESTIONNAIRES_QUERY_KEY }),
      queryClient.invalidateQueries({ queryKey: PHOTO_REQUESTS_QUERY_KEY }),
      queryClient.invalidateQueries({ queryKey: PHOTOS_QUERY_KEY }),
      id ? queryClient.invalidateQueries({ queryKey: photoRequestQueryKey(id) }) : Promise.resolve(),
    ]);
  };
}

export function useAcceptPhotoRequest() {
  return useMutation({ mutationFn: (id: string) => acceptPhotoRequest(id) });
}

/** Uploads one picked photo to a request. The photo is on the request as soon as this succeeds. */
export function useUploadRequestPhoto() {
  return useMutation({
    mutationFn: ({ photo, target }: { photo: PickedPhoto; target: RequestPhotoTarget }) =>
      uploadRequestPhoto(photo, target),
  });
}

/** Sends the request's uploaded photos to the coach and completes the request. */
export function useSubmitPhotoRequest() {
  return useMutation({ mutationFn: (id: string) => submitPhotoRequest(id) });
}

export function useDismissPhotoRequest() {
  return useMutation({
    mutationFn: ({ id, reason }: { id: string; reason: string | undefined }) => dismissPhotoRequest(id, reason),
  });
}
