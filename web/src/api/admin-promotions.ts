/**
 * Admin promotions API module.
 *
 * Wraps the NSwag-generated /admin/promotions endpoints.
 */
import { apiClient } from '@/api/client';
import {
  PromotionDuration,
  PromotionKind,
  PromotionMode,
  PromotionStatus,
  PromotionStatusFilter,
} from '@/api/generated';
import type { CreatePromotionRequest, PromotionDto } from '@/api/generated';

export { PromotionDuration, PromotionKind, PromotionMode, PromotionStatus, PromotionStatusFilter };
export type { CreatePromotionRequest, PromotionDto };

export const adminPromotionsKey = ['admin', 'promotions'] as const;

/** Lists the promotions of one tab via GET /admin/promotions. */
export async function getAdminPromotions(status: PromotionStatusFilter): Promise<PromotionDto[]> {
  const response = await apiClient.listPromotionsEndpoint(status);
  return response.items ?? [];
}

/** Creates a promotion (and its Stripe coupon) via POST /admin/promotions. */
export async function createAdminPromotion(request: CreatePromotionRequest): Promise<PromotionDto> {
  return apiClient.createPromotionEndpoint(request);
}
