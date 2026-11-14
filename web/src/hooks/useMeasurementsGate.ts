import type { GetMeasurementsResponse } from '@/api/generated';
import { useSubscription } from '@/hooks/useSubscriptionQueries';

const NO_MEASUREMENTS: GetMeasurementsResponse = { items: [], totalCount: 0, page: 1 };

/**
 * Options for every client-measurements query. A plan without weight and measurements is refused with 403,
 * so the request is not sent; the empty placeholder keeps readers of the query out of a pending or error state.
 * `locked` is what the UI shows the paywall notice on.
 */
export function useMeasurementsGate() {
  const { isEntitled } = useSubscription();
  const allowed = isEntitled('canUseWeightAndMeasurements');
  return {
    locked: !allowed,
    enabled: allowed,
    placeholderData: allowed ? undefined : NO_MEASUREMENTS,
  };
}
