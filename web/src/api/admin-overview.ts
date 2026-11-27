/**
 * Admin overview API module.
 *
 * Wraps GET /admin/overview/attention. The Last 30 days tiles reuse the Reports endpoint
 * (`useAdminReports` in components/admin-reports).
 */
import { apiClient } from '@/api/client';
import type {
  AdminAttentionCoachDto,
  AdminAttentionPromotionDto,
  GetAdminAttentionResponse,
} from '@/api/generated';

export type { AdminAttentionCoachDto, AdminAttentionPromotionDto, GetAdminAttentionResponse };

export const adminAttentionKey = ['admin', 'overview', 'attention'] as const;

/** The "needs attention" counts and examples, via GET /admin/overview/attention. */
export async function getAdminAttention(signal?: AbortSignal): Promise<GetAdminAttentionResponse> {
  return apiClient.getAdminAttentionEndpoint(signal);
}
