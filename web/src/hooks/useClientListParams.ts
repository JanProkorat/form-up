import { useCallback, useMemo } from 'react';
import { useSearchParams } from 'react-router-dom';
import { ClientPlanFilter } from '@/api/generated';

/**
 * The clients-list page's tabs. `All` | `Active` | `OutsidePlan` | `Archived` mirror the
 * generated `ClientListTab`; `Pending` is a page-only virtual tab with no backend member.
 * `Pending` drives the Pending-tab rows instead of the list query.
 */
export type ClientsPageTab = 'All' | 'Active' | 'OutsidePlan' | 'Pending' | 'Archived';

// Legacy ?tab=Paused, ?chip=, ?endingSoon= and ?outsidePlan= are not valid values and fall back to the defaults.
const VALID_TABS: readonly ClientsPageTab[] = ['All', 'Active', 'OutsidePlan', 'Pending', 'Archived'];
const DEFAULT_TAB: ClientsPageTab = 'Active';

const VALID_PLANS: readonly ClientPlanFilter[] = [
  ClientPlanFilter.Active,
  ClientPlanFilter.None,
  ClientPlanFilter.EndingSoon,
];

export const CLIENTS_PAGE_SIZE = 20;

export interface ClientListFilters {
  tab: ClientsPageTab;
  search: string;
  /** The single-choice plan filter; null is "All plans". Ignored on the Pending tab. */
  plan: ClientPlanFilter | null;
  tagIds: string[];
  page: number;
  pageSize: number;
}

export interface UseClientListParamsResult {
  filters: ClientListFilters;
  /**
   * Sets the search text. Uses `replace` (not `push`) so that Back doesn't
   * walk one step per keystroke; resets page to 1.
   */
  setSearch: (value: string) => void;
  /** Switches tab. Pushes a history entry; resets page to 1. */
  setTab: (tab: ClientsPageTab) => void;
  /** Sets the plan filter (null = all plans). Pushes a history entry; resets page to 1. */
  setPlan: (plan: ClientPlanFilter | null) => void;
  /** Replaces the full selected-tag set. Pushes a history entry; resets page to 1. */
  setTagIds: (tagIds: string[]) => void;
  /** Navigates to a page. Pushes a history entry. */
  setPage: (page: number) => void;
  /** Resets the plan filter to all plans. Pushes a history entry; resets page to 1. */
  clearPlan: () => void;
  /** Clears plan, tags and search back to defaults; keeps the current tab. */
  clearFilters: () => void;
}

function parseTab(value: string | null): ClientsPageTab {
  return VALID_TABS.includes(value as ClientsPageTab) ? (value as ClientsPageTab) : DEFAULT_TAB;
}

function parsePlan(value: string | null): ClientPlanFilter | null {
  return VALID_PLANS.includes(value as ClientPlanFilter) ? (value as ClientPlanFilter) : null;
}

function parsePage(value: string | null): number {
  const parsed = Number(value);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : 1;
}

function parseTagIds(value: string | null): string[] {
  if (!value) {
    return [];
  }
  const ids = value
    .split(',')
    .map((id) => id.trim())
    .filter(Boolean);
  return Array.from(new Set(ids));
}

type UrlPatch = Partial<Record<'tab' | 'plan' | 'q' | 'tags' | 'page', string | null>>;

/**
 * Owns the clients-list page's filter state entirely in the URL — `tab`,
 * `plan`, `q`, `tags`, `page`. Component state holds only what isn't a
 * filter (the raw, undebounced search input, row selection, dialog-open
 * flags). This is the sole source consumed to build the list query key —
 * there is no second copy of this state to drift out of sync with it.
 *
 * Read-side normalisation (`parseTab`/`parsePlan`/`parsePage`) means a
 * hand-edited or stale URL can never send a bad tab or plan to the API:
 * unknown values silently fall back to the default. Unknown tag ids are
 * deliberately NOT dropped here — the backend treats an unrecognised or
 * foreign tag id as matching nothing (see `getClientsEndpoint` doc comment
 * in `generated.ts`), so it is safe to forward as-is; the tag *picker*
 * (phase 3) is the layer that should stop rendering a ghost chip for a
 * tag id no longer present in `useClientTags()`'s result, via `setTagIds`.
 */
export function useClientListParams(): UseClientListParamsResult {
  const [searchParams, setSearchParams] = useSearchParams();

  const filters = useMemo<ClientListFilters>(() => {
    return {
      tab: parseTab(searchParams.get('tab')),
      search: searchParams.get('q') ?? '',
      plan: parsePlan(searchParams.get('plan')),
      tagIds: parseTagIds(searchParams.get('tags')),
      page: parsePage(searchParams.get('page')),
      pageSize: CLIENTS_PAGE_SIZE,
    };
  }, [searchParams]);

  const update = useCallback(
    (patch: UrlPatch, options: { replace?: boolean } = {}) => {
      setSearchParams(
        (previous) => {
          const next = new URLSearchParams(previous);
          for (const [key, value] of Object.entries(patch)) {
            if (value === null || value === '') {
              next.delete(key);
            } else {
              next.set(key, value);
            }
          }
          return next;
        },
        { replace: options.replace ?? false },
      );
    },
    [setSearchParams],
  );

  const setSearch = useCallback(
    (value: string) => {
      update({ q: value || null, page: null }, { replace: true });
    },
    [update],
  );

  const setTab = useCallback(
    (tab: ClientsPageTab) => {
      // The plan choice is kept across tabs; only the page resets.
      update({ tab: tab === DEFAULT_TAB ? null : tab, page: null });
    },
    [update],
  );

  const setPlan = useCallback(
    (plan: ClientPlanFilter | null) => {
      update({ plan, page: null });
    },
    [update],
  );

  const setTagIds = useCallback(
    (tagIds: string[]) => {
      const deduped = Array.from(new Set(tagIds));
      update({ tags: deduped.length ? deduped.join(',') : null, page: null });
    },
    [update],
  );

  const setPage = useCallback(
    (page: number) => {
      update({ page: page > 1 ? String(page) : null });
    },
    [update],
  );

  const clearPlan = useCallback(() => {
    update({ plan: null, page: null });
  }, [update]);

  const clearFilters = useCallback(() => {
    update({ plan: null, tags: null, q: null, page: null });
  }, [update]);

  return { filters, setSearch, setTab, setPlan, setTagIds, setPage, clearPlan, clearFilters };
}
