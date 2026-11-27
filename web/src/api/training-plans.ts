import api from '@/lib/api';
import type {
  RawTrainingPlanDetail,
  GetTrainingPlansResponse,
  CreateTrainingPlanRequest,
  UpdateTrainingPlanRequest,
  ExerciseProgressResponse,
} from './training-plan-types';

/** Fetch paginated list of training plans. */
export async function getTrainingPlans(params: {
  clientId?: string;
  status?: string;
  page?: number;
  pageSize?: number;
}): Promise<GetTrainingPlansResponse> {
  const { data } = await api.get<GetTrainingPlansResponse>('/training/plans', { params });
  return data;
}

/** Get a single training plan by ID. */
export async function getTrainingPlan(planId: string): Promise<RawTrainingPlanDetail> {
  const { data } = await api.get<RawTrainingPlanDetail>(`/training/plans/${planId}`);
  return data;
}

/** Create a new training plan. */
export async function createTrainingPlan(request: CreateTrainingPlanRequest): Promise<RawTrainingPlanDetail> {
  const { data } = await api.post<RawTrainingPlanDetail>('/training/plans', request);
  return data;
}

/** Full-state update of a training plan. */
export async function updateTrainingPlan(
  planId: string,
  request: UpdateTrainingPlanRequest,
): Promise<RawTrainingPlanDetail> {
  const { data } = await api.put<RawTrainingPlanDetail>(`/training/plans/${planId}`, request);
  return data;
}

/** Delete a training plan. */
export async function deleteTrainingPlan(planId: string): Promise<void> {
  await api.delete(`/training/plans/${planId}`);
}

/** Mark a training plan as completed. */
export async function completeTrainingPlan(
  planId: string,
  version: number,
): Promise<RawTrainingPlanDetail> {
  const { data } = await api.post<RawTrainingPlanDetail>(
    `/training/plans/${planId}/complete`,
    { version },
  );
  return data;
}

/** Link or unlink a questionnaire response to a training plan. */
export async function linkTrainingQuestionnaire(
  planId: string,
  questionnaireResponseId: string | null,
  version: number,
): Promise<RawTrainingPlanDetail> {
  const { data } = await api.put<RawTrainingPlanDetail>(
    `/training/plans/${planId}/link-questionnaire`,
    { questionnaireResponseId, version },
  );
  return data;
}

/** Response from the retroactive session-finish endpoint. */
export interface FinishSessionResponse {
  workoutLogId: string;
  planId: string;
  sessionId: string;
  completedAt: string;
}

/**
 * Retroactively mark a past unfinished session as completed on behalf of the
 * client. Only applicable to sessions the client skipped or never started.
 * The caller must supply `completedAt` as the session's scheduled calendar
 * date in UTC ISO-8601 so history attributes to the correct historical day.
 *
 * Errors:
 *   404 — plan not found / not owned, or session not in plan
 *   409 — SESSION_ALREADY_COMPLETED
 *   400 — COMPLETED_AT_IN_FUTURE
 *   422 — COMPLETED_AT_BEFORE_PLAN_START
 */
export async function finishSession(
  planId: string,
  sessionId: string,
  completedAt: string,
): Promise<FinishSessionResponse> {
  const { data } = await api.post<FinishSessionResponse>(
    `/trainer/training/plans/${planId}/sessions/${sessionId}/finish`,
    { completedAt },
  );
  return data;
}

/** Get exercise progress for a client. */
export async function getExerciseProgress(
  clientId: string,
  exerciseId: string,
): Promise<ExerciseProgressResponse> {
  const { data } = await api.get<ExerciseProgressResponse>(
    `/training/clients/${clientId}/progress/${exerciseId}`,
  );
  return data;
}

/**
 * Acquires an Editing lock on a published training session, allowing the trainer to edit it.
 *
 * Returns 204 on success.
 * Returns 409 (errorCode: "session_locked") when the session is Live (client is
 * training) or already locked by another party.
 */
export async function unlockTrainingSession(
  planId: string,
  sessionId: string,
): Promise<void> {
  await api.post(`/training/plans/${planId}/sessions/${sessionId}/unlock`);
}

/**
 * Releases the Editing lock on a training session, returning it to Stable state.
 *
 * Idempotent: succeeds (204) even when the lock was already released or expired.
 */
export async function relockTrainingSession(
  planId: string,
  sessionId: string,
): Promise<void> {
  await api.post(`/training/plans/${planId}/sessions/${sessionId}/relock`);
}
