import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  adminPromotionsKey,
  createAdminPromotion,
  endAdminPromotion,
  getAdminPromotions,
  type CreatePromotionRequest,
  type PromotionStatusFilter,
} from '@/api/admin-promotions';
import { getErrorStatus } from '@/lib/api-errors';

/** The promotions of one tab. Errors are handled by the page (retry). */
export function useAdminPromotions(status: PromotionStatusFilter) {
  return useQuery({
    queryKey: [...adminPromotionsKey, status],
    queryFn: () => getAdminPromotions(status),
    retry: false,
  });
}

/** Creates a promotion, then refetches every tab so the new row shows under the right filter. */
export function useCreatePromotion() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: CreatePromotionRequest) => createAdminPromotion(request),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: adminPromotionsKey }),
  });
}

/**
 * Ends a promotion, then refetches every tab. A 404 or 409 means the list was stale (the promotion is gone or
 * already ended), so those refetch too; a provider or configuration failure leaves the rows as they are.
 */
export function useEndPromotion() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (promotionId: string) => endAdminPromotion(promotionId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: adminPromotionsKey }),
    onError: (error) => {
      const status = getErrorStatus(error);
      if (status === 404 || status === 409) {
        void queryClient.invalidateQueries({ queryKey: adminPromotionsKey });
      }
    },
  });
}
