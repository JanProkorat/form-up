import { useQuery } from '@tanstack/react-query';

import { PHOTOS_QUERY_KEY, PHOTOS_STALE_MS, fetchPhotoMonths } from '@/api/photos';
import { useAuthStore } from '@/stores/auth';

type UsePhotosOptions = {
  /** Fetch again on every mount, even while the cached list is fresh. */
  fresh?: boolean;
};

/** The client's progress photos by month; refetched before their signed URLs can expire. */
export function usePhotos({ fresh = false }: UsePhotosOptions = {}) {
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  return useQuery({
    queryKey: PHOTOS_QUERY_KEY,
    queryFn: ({ signal }) => fetchPhotoMonths(signal),
    enabled: isAuthenticated,
    staleTime: PHOTOS_STALE_MS,
    refetchOnMount: fresh ? 'always' : true,
    retry: 1,
  });
}
