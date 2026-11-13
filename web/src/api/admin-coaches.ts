/**
 * Admin coaches API module.
 *
 * Wraps the /admin/coaches endpoints. The list goes through the plain axios instance because the
 * generated client drops the X-Total-Count header the pager needs.
 */
import api from '@/lib/api';
import { apiClient } from '@/api/client';
import {
  AdminCoachHistoryKind,
  AdminCoachStatus,
  AdminCoachStatusFilter,
  BillingInterval,
} from '@/api/generated';
import type {
  AdminCoachHistoryEntryDto,
  AdminCoachPlanDto,
  AdminCoachRowDto,
  AdminCoachStatsDto,
  CancelCoachRenewalResponse,
  DisableCoachAccountByAdminResponse,
  EndCoachPlanResponse,
  GetAdminCoachResponse,
  GiveCoachPlanFreeRequest,
  GiveCoachPlanFreeResponse,
  ListAdminCoachesResponse,
} from '@/api/generated';

export { AdminCoachHistoryKind, AdminCoachStatus, AdminCoachStatusFilter, BillingInterval };
export type {
  AdminCoachHistoryEntryDto,
  AdminCoachPlanDto,
  AdminCoachRowDto,
  AdminCoachStatsDto,
  CancelCoachRenewalResponse,
  DisableCoachAccountByAdminResponse,
  EndCoachPlanResponse,
  GetAdminCoachResponse,
  GiveCoachPlanFreeRequest,
  GiveCoachPlanFreeResponse,
};

export const adminCoachesKey = ['admin', 'coaches'] as const;
export const adminCoachesListKey = [...adminCoachesKey, 'list'] as const;
export const adminCoachDetailKey = (coachId: string) => [...adminCoachesKey, 'detail', coachId] as const;

export const COACHES_PAGE_SIZE = 20;

export interface AdminCoachesQuery {
  page: number;
  status: AdminCoachStatusFilter;
  search: string;
}

export interface AdminCoachesPage extends ListAdminCoachesResponse {
  /** Coaches matching the filter and search, across all pages (X-Total-Count). */
  totalCount: number;
}

/** One page of coaches plus the stats over all coaches, via GET /admin/coaches. */
export async function getAdminCoaches(query: AdminCoachesQuery, signal?: AbortSignal): Promise<AdminCoachesPage> {
  const search = query.search.trim();
  const { data, headers } = await api.get<ListAdminCoachesResponse>('/admin/coaches', {
    params: {
      page: query.page,
      pageSize: COACHES_PAGE_SIZE,
      status: query.status,
      ...(search ? { search } : {}),
    },
    signal,
  });
  const totalCount = parseInt(String(headers['x-total-count'] ?? '0'), 10);
  return { ...data, totalCount: Number.isNaN(totalCount) ? 0 : totalCount };
}

/** One coach for the drawer, via GET /admin/coaches/{coachId}. */
export async function getAdminCoach(coachId: string, signal?: AbortSignal): Promise<GetAdminCoachResponse> {
  return apiClient.getAdminCoachEndpoint(coachId, signal);
}

/** Gives the coach a paid plan for free via POST /admin/coaches/{coachId}/give-free. */
export async function giveCoachPlanFree(
  coachId: string,
  request: GiveCoachPlanFreeRequest,
): Promise<GiveCoachPlanFreeResponse> {
  return apiClient.giveCoachPlanFreeEndpoint(coachId, request);
}

/** Cancels the coach's renewal via POST /admin/coaches/{coachId}/cancel-renewal. */
export async function cancelCoachRenewal(coachId: string, reason: string): Promise<CancelCoachRenewalResponse> {
  return apiClient.cancelCoachRenewalEndpoint(coachId, { reason });
}

/** Disables the coach's account via POST /admin/coaches/{coachId}/disable. */
export async function disableCoachAccount(
  coachId: string,
  reason: string,
): Promise<DisableCoachAccountByAdminResponse> {
  return apiClient.disableCoachAccountByAdminEndpoint(coachId, { reason });
}

/** Ends the coach's paid or given plan now via POST /admin/coaches/{coachId}/end-plan. */
export async function endCoachPlan(coachId: string, reason: string): Promise<EndCoachPlanResponse> {
  return apiClient.endCoachPlanEndpoint(coachId, { reason });
}
