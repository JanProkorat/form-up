import api from '@/lib/api';
import { apiClient } from '@/api/client';
import type {
  GetClientCheckInHistoryResponse,
  GetTrainerCheckInsResponse,
  TrainerCheckInDto,
} from '@/api/generated';

export type { GetTrainerCheckInsResponse, TrainerCheckInDto };

/**
 * Profession values as serialized by the backend (JsonStringEnumConverter not applied
 * here — Profession is stored as a C# string property, always "Training" or "Nutrition").
 */
export type Profession = 'Training' | 'Nutrition';

/* ─────────────────────── WeeklyCheckInStatus ────────────────────────────────────── */

/**
 * Mirrors the C# WeeklyCheckInStatus enum (added in #331).
 * Values are serialized as strings by the backend (JsonStringEnumConverter).
 */
export type WeeklyCheckInStatus =
  | 'Pending'
  | 'Responded'
  | 'Dismissed'
  | 'Reviewed'
  | 'Expired';

/* ─────────────────────── CheckInFlag ───────────────────────────────────────────── */

/**
 * Mirrors the C# CheckInFlag enum.
 * Values are serialized as strings by the backend (JsonStringEnumConverter).
 */
export type CheckInFlag =
  | 'Traveling'
  | 'EventOrCelebration'
  | 'SickOrLowEnergy'
  | 'InjuryOrPain'
  | 'MoreTimeAvailable'
  | 'LessTimeAvailable';

/* ─────────────────────── GET /trainer/weekly-check-ins?weekStartDate=... ────────── */

/**
 * GET /trainer/weekly-check-ins?weekStartDate=YYYY-MM-DD
 *
 * @param weekStartDate - Optional. Omit to get the trainer's active check-ins
 *   (not dismissed by the client, not yet reviewed by the trainer) across all
 *   weeks — pending, responded, and expired. Pass an ISO Monday date to
 *   preserve the exact-week filter (kept for a future history view).
 */
export async function getTrainerCheckIns(
  weekStartDate?: string,
): Promise<GetTrainerCheckInsResponse> {
  const { data } = await api.get<GetTrainerCheckInsResponse>('/trainer/weekly-check-ins', {
    params: weekStartDate ? { weekStartDate } : {},
  });
  return data;
}

/* ─────────────────────── GET /trainer/weekly-check-ins/{id} ─────────────────────── */

/**
 * Detail check-in DTO.
 * Mirrors GetCheckInDetailResponse.cs.
 */
export interface CheckInDetailDto {
  id: string;
  clientUserId: string;
  clientName: string;
  professionalUserId: string;
  profession: Profession;
  weekStartDate: string;
  flags: CheckInFlag[];
  note: string | null;
  sentAt: string;
  respondedAt: string | null;
  dismissedByClientAt: string | null;
  reviewedByTrainerAt: string | null;
  /** Added in #331 — lifecycle status. */
  status: WeeklyCheckInStatus;
  /** Added in #331 — deadline UTC datetime; null if no deadline. */
  dueAt: string | null;
  /** Hunger score (1-5) the client gave; null when none was given. */
  hungerScore: number | null;
}

/** GET /trainer/weekly-check-ins/{id} */
export async function getCheckInDetail(id: string): Promise<CheckInDetailDto> {
  const { data } = await api.get<CheckInDetailDto>(`/trainer/weekly-check-ins/${id}`);
  return data;
}

/* ─────────────────────── POST /trainer/weekly-check-ins/{id}/mark-reviewed ─────── */

/** Response for POST /trainer/weekly-check-ins/{id}/mark-reviewed. */
export interface MarkCheckInReviewedResponse {
  id: string;
  reviewedAt: string; // ISO datetime (UTC)
}

/** POST /trainer/weekly-check-ins/{id}/mark-reviewed */
export async function markCheckInReviewed(id: string): Promise<MarkCheckInReviewedResponse> {
  const { data } = await api.post<MarkCheckInReviewedResponse>(
    `/trainer/weekly-check-ins/${id}/mark-reviewed`,
  );
  return data;
}

/* ─────────────────────── GET /trainer/clients/{clientUserId}/weekly-check-ins/history ─ */

/** Slot status after narrowing the server's plain string. */
export type CheckInHistoryStatus = 'answered' | 'missed' | 'waiting' | 'none';

/** Narrows the history slot's status string; anything unrecognised counts as "none". */
export function toCheckInHistoryStatus(value: string | null | undefined): CheckInHistoryStatus {
  switch (value) {
    case 'answered':
    case 'missed':
    case 'waiting':
      return value;
    default:
      return 'none';
  }
}

/**
 * GET /trainer/clients/{clientUserId}/weekly-check-ins/history
 * @param clientUserId - Client's ApplicationUser Id (Guid string), not the route PublicId.
 * @param weeks        - Number of weekly slots, oldest first.
 */
export function getClientCheckInHistory(
  clientUserId: string,
  weeks: number,
): Promise<GetClientCheckInHistoryResponse> {
  return apiClient.getClientCheckInHistoryEndpoint(clientUserId, weeks);
}
