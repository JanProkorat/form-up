import { useQuery } from '@tanstack/react-query';
import { adminAttentionKey, getAdminAttention } from '@/api/admin-overview';

/** The "needs attention" block of the admin overview. */
export function useAdminAttention() {
  return useQuery({
    queryKey: adminAttentionKey,
    queryFn: ({ signal }) => getAdminAttention(signal),
    retry: false,
  });
}
