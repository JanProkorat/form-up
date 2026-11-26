import api from '@/lib/api';
import { apiClient } from '@/api/client';
import type { CheckPendingInviteSlotResponse, LinkCapabilityScope } from '@/api/generated';

export interface PendingInviteDto {
  publicId: string;
  email: string;
  message?: string | null;
  sentAt: string;
  isAccepted: boolean;
  questionnairePublicId?: string | null;
  questionnaireTitle?: string | null;
}

export interface CreatePendingInviteRequest {
  email: string;
  message?: string | null;
  questionnairePublicId?: string | null;
  /** Only sent by a professional holding both roles; omitted so the server defaults to the held roles. */
  requestedScope?: LinkCapabilityScope;
  /** Public id of the package the client is assigned when they accept. */
  packageId?: string;
  /** Public id of a discount on `packageId`; needs a package. */
  discountId?: string;
}

export interface CreatePendingInviteResponse {
  publicId: string;
  email: string;
  sentAt: string;
  questionnairePublicId?: string | null;
}

export interface GetPendingInvitesResponse {
  invites: PendingInviteDto[];
}

export async function createPendingInvite(
  request: CreatePendingInviteRequest,
): Promise<CreatePendingInviteResponse> {
  const { data } = await api.post<CreatePendingInviteResponse>(
    '/trainer/pending-invites',
    request,
  );
  return data;
}

export async function getPendingInvites(): Promise<GetPendingInvitesResponse> {
  const { data } = await api.get<GetPendingInvitesResponse>(
    '/trainer/pending-invites',
  );
  return data;
}

/**
 * Which of the caller's professions are already taken for this invitee, via
 * GET /trainer/pending-invites/slot-check. Errors: 400 bad email, 429 rate limit.
 */
export async function checkPendingInviteSlot(
  email: string,
  signal?: AbortSignal,
): Promise<CheckPendingInviteSlotResponse> {
  return apiClient.checkPendingInviteSlotEndpoint(email, signal);
}

export async function deletePendingInvite(id: string): Promise<void> {
  await api.delete(`/trainer/pending-invites/${id}`);
}
