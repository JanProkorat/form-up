import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  adminPromotionsKey,
  createAdminPromotion,
  getAdminPromotions,
  type CreatePromotionRequest,
  type PromotionStatusFilter,
} from '@/api/admin-promotions';

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
