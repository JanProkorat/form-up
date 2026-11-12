/**
 * Trainer clients-list API module.
 *
 * Wraps the NSwag-generated `getClientsEndpoint` / `getPendingClientsEndpoint`.
 */
import { apiClient } from '@/api/client';
import type {
  ClientSummary,
  ClientListStatus,
  ClientListFilter,
  ClientTabCounts,
  ClientFilterCounts,
  PendingClientRow,
} from '@/api/generated';

export type {
  ClientSummary,
  ClientListStatus,
  ClientListFilter,
  ClientTabCounts,
  ClientFilterCounts,
  PendingClientRow,
};
export { PendingRowKind } from '@/api/generated';

export interface GetClientsParams {
  page: number;
  pageSize: number;
  tagIds: string[];
  search?: string;
  status?: ClientListStatus;
  filter?: ClientListFilter;
  /** Narrows the list to live clients outside the plan in force. Never combine with the Archived status (400). */
  outsidePlan?: boolean;
}

export interface GetClientsResult {
  clients: ClientSummary[];
  totalCount: number;
  page: number;
  pageSize: number;
  tabCounts: ClientTabCounts;
  filterCounts: ClientFilterCounts;
}

/**
 * Lists the trainer's clients via GET /trainer/clients.
 *
 * `tagIds` is always passed through as-is (even empty) — the generated
 * client serialises it as a repeated `tagIds=` query parameter, which is
 * why this endpoint is wrapped here rather than hand-built: constructing
 * that URL by hand is exactly how the tag filter goes silently wrong.
 */
export async function getClients(params: GetClientsParams): Promise<GetClientsResult> {
  const response = await apiClient.getClientsEndpoint(
    params.page,
    params.pageSize,
    params.tagIds,
    params.search,
    params.status,
    params.filter,
    params.outsidePlan,
  );
  return {
    clients: response.clients ?? [],
    totalCount: response.totalCount ?? 0,
    page: response.page ?? params.page,
    pageSize: response.pageSize ?? params.pageSize,
    tabCounts: response.tabCounts ?? {},
    filterCounts: response.filterCounts ?? {},
  };
}

/**
 * Lists the trainer's Pending-tab rows (unaccepted invites plus incoming
 * client requests) via GET /trainer/clients/pending.
 */
export async function getPendingClients(): Promise<PendingClientRow[]> {
  const response = await apiClient.getPendingClientsEndpoint();
  return response.rows ?? [];
}
