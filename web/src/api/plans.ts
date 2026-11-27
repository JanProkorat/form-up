import api from '@/lib/api';
import { apiClient } from '@/api/client';
import type {
  CopyNutritionPlanRequest,
  CopyNutritionPlanResponse,
  CreatePlanRequest,
  GetPlanResponse,
  PlanSummaryDto,
  UpdatePlanRequest as UpdatePlanDocumentRequest,
} from '@/api/generated';
import type {
  NutritionPlanDetail,
  GetPlansResponse,
  UpdatePlanRequest,
} from './plan-types';

/** Fetch paginated list of nutrition plans. */
export async function getPlans(params: {
  clientId?: string;
  status?: string;
  page?: number;
  pageSize?: number;
}): Promise<GetPlansResponse> {
  const { data } = await api.get<GetPlansResponse>('/nutrition/plans', { params });
  return data;
}

/** Get a single nutrition plan by ID. */
export async function getPlan(planId: string): Promise<NutritionPlanDetail> {
  const { data } = await api.get<NutritionPlanDetail>(`/nutrition/plans/${planId}`);
  return data;
}

/** Create a new nutrition plan (POST /nutrition/plans); resolves with its summary. */
export function createPlan(request: CreatePlanRequest): Promise<PlanSummaryDto> {
  return apiClient.createPlanEndpoint(request);
}

/** Full-state update of a nutrition plan. */
export async function updatePlan(
  planId: string,
  request: UpdatePlanRequest,
): Promise<NutritionPlanDetail> {
  const { data } = await api.put<NutritionPlanDetail>(`/nutrition/plans/${planId}`, request);
  return data;
}

/** Delete a nutrition plan. */
export async function deletePlan(planId: string): Promise<void> {
  await api.delete(`/nutrition/plans/${planId}`);
}

/** Mark a nutrition plan as completed. */
export async function completePlan(
  planId: string,
  version: number,
): Promise<NutritionPlanDetail> {
  const { data } = await api.post<NutritionPlanDetail>(
    `/nutrition/plans/${planId}/complete`,
    { version },
  );
  return data;
}

/** Link or unlink a questionnaire response to a nutrition plan. */
export async function linkQuestionnaire(
  planId: string,
  questionnaireResponseId: string | null,
  version: number,
): Promise<NutritionPlanDetail> {
  const { data } = await api.put<NutritionPlanDetail>(
    `/nutrition/plans/${planId}/link-questionnaire`,
    { questionnaireResponseId, version },
  );
  return data;
}

/** Publish a single week of a nutrition plan. */
export async function publishWeek(
  planId: string,
  weekNumber: number,
  version: number,
): Promise<NutritionPlanDetail> {
  const { data } = await api.post<NutritionPlanDetail>(
    `/nutrition/plans/${planId}/weeks/${weekNumber}/publish`,
    { version },
  );
  return data;
}

/** Copy a nutrition plan to a client as a new Draft plan (POST /nutrition/plans/{planId}/copy). */
export function copyNutritionPlan(
  planId: string,
  request: CopyNutritionPlanRequest,
): Promise<CopyNutritionPlanResponse> {
  return apiClient.copyNutritionPlanEndpoint(planId, request);
}

/** The plan as the editor loads it (GET /nutrition/plans/{planId}), typed from the generated client. */
export function fetchPlanForEditor(planId: string): Promise<GetPlanResponse> {
  return apiClient.getPlanEndpoint(planId);
}

/** Full-state save of a plan from the editor (PUT /nutrition/plans/{planId}). */
export function savePlanFromEditor(planId: string, request: UpdatePlanDocumentRequest): Promise<GetPlanResponse> {
  return apiClient.updatePlanEndpoint(planId, request);
}

/** Hard-deletes a plan no published week of which has started (DELETE /nutrition/plans/{planId}). */
export function deletePlanFromEditor(planId: string): Promise<void> {
  return apiClient.deletePlanEndpoint(planId);
}

/** Finishes an Active plan at the given version (POST /nutrition/plans/{planId}/complete); returns the plan. */
export function completePlanFromEditor(planId: string, version: number | undefined): Promise<GetPlanResponse> {
  return apiClient.completePlanEndpoint(planId, { version });
}

/** Publishes one stored week (POST /nutrition/plans/{planId}/weeks/{weekNumber}/publish); returns the plan. */
export function publishPlanWeekFromEditor(
  planId: string,
  weekNumber: number,
  version: number | undefined,
): Promise<GetPlanResponse> {
  return apiClient.publishWeekEndpoint(planId, weekNumber, { version });
}

/** Unpublishes one stored week (POST /nutrition/plans/{planId}/weeks/{weekNumber}/unpublish); returns the plan. */
export function unpublishPlanWeekFromEditor(planId: string, weekNumber: number): Promise<GetPlanResponse> {
  return apiClient.unpublishNutritionWeekEndpoint(planId, weekNumber);
}
