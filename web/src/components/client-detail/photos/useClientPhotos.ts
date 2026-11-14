import { useCallback, useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { getClientPhotoGroups } from '@/api/client-photos';
import { getClientMeasurements } from '@/api/measurements';
import { useMeasurementsGate } from '@/hooks/useMeasurementsGate';
import {
  flattenGroups,
  nearestWeighIn,
  rangeFrom,
  weighInWindow,
  type PhotoRange,
} from '@/components/client-detail/photos/photosModel';

/**
 * Pre-signed photo URLs expire after 15 minutes. The query goes stale well
 * before that and refetches on a timer, so the URLs in the cache stay usable
 * (a deliberate exception to the no-polling rule in the data-fetching rules).
 */
export const PHOTOS_STALE_MS = 5 * 60 * 1000;
export const PHOTOS_REFETCH_MS = 10 * 60 * 1000;

const WEIGH_IN_PAGE_SIZE = 100;

/** The client's photos in a range, grouped by month, with the URL-refresh policy applied. */
export function useClientPhotos(clientId: string, range: PhotoRange) {
  const from = rangeFrom(range);

  const query = useQuery({
    queryKey: ['client-photos', clientId, from ?? null, null],
    queryFn: () => getClientPhotoGroups({ clientId, from }),
    enabled: Boolean(clientId),
    staleTime: PHOTOS_STALE_MS,
    refetchInterval: PHOTOS_REFETCH_MS,
  });

  const groups = query.data?.groups;
  const photos = useMemo(() => flattenGroups(groups), [groups]);

  const { refetch } = query;
  /** Called when an image fails to load (expired URL): fetch fresh URLs without restarting a running fetch. */
  const refreshUrls = useCallback(() => {
    void refetch({ cancelRefetch: false });
  }, [refetch]);

  return { query, groups, photos, refreshUrls };
}

/** The weigh-in (a measurement with a weight) closest to a photo's date, within a window around it. */
export function useNearestWeighIn(clientId: string, takenAt: string | undefined) {
  const window = weighInWindow(takenAt);
  const measurementsGate = useMeasurementsGate();

  const query = useQuery({
    queryKey: ['client-measurements', clientId, 'around', window?.from ?? null, window?.to ?? null],
    queryFn: () => getClientMeasurements(clientId, 1, WEIGH_IN_PAGE_SIZE, window?.from, window?.to),
    enabled: Boolean(clientId) && window !== undefined && measurementsGate.enabled,
    placeholderData: measurementsGate.placeholderData,
  });

  const measurement = useMemo(() => nearestWeighIn(query.data?.items, takenAt), [query.data, takenAt]);
  return {
    measurement,
    isPending: query.isPending && query.fetchStatus !== 'idle',
    /** The lookup failed and nothing is cached, so "no weigh-in" would be a false claim. */
    isError: query.isError && query.data === undefined,
    refetch: () => void query.refetch(),
  };
}
