import { useCallback, useMemo } from 'react';
import { useSearchParams } from 'react-router-dom';

/**
 * The clients-list page's tabs. `Active` | `Paused` | `Archived` mirror
 * `ClientListStatus`; `Pending` and `All` are page-only virtual tabs with no
 * backend enum member. `Pending` drives the Pending-tab query instead of the
 * list query; `All` lists Active + Paused clients (no status filter) with the
 * pending rows below them.
 */
export type ClientListTab = 'All' | 'Active' | 'Paused' | 'Archived' | 'Pending';

// A stale ?tab=OutsidePlan (the removed tab) or ?chip= is not a valid value and falls back to the defaults.
const VALID_TABS: readonly ClientListTab[] = ['All', 'Active', 'Paused', 'Archived', 'Pending'];
const DEFAULT_TAB: ClientListTab = 'All';

/** The independent filter flags of the Filter menu; both can be on at once. */
export type ClientListFlag = 'endingSoon' | 'outsidePlan';

const FLAG_PARAM: Record<ClientListFlag, string> = {
  endingSoon: 'endingSoon',
  outsidePlan: 'outsidePlan',
};

export const CLIENTS_PAGE_SIZE = 20;

export interface ClientListFilters {
  tab: ClientListTab;
  search: string;
  /** Plans ending soon. Ignored on the Pending tab. */
  endingSoon: boolean;
  /** Clients outside the plan. Never on while the Archived tab is open (the backend rejects the pair). */
  outsidePlan: boolean;
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
  setTab: (tab: ClientListTab) => void;
  /** Turns one filter flag on or off. Pushes a history entry; resets page to 1. */
  setFlag: (flag: ClientListFlag, on: boolean) => void;
  /** Replaces the full selected-tag set. Pushes a history entry; resets page to 1. */
  setTagIds: (tagIds: string[]) => void;
  /** Navigates to a page. Pushes a history entry. */
  setPage: (page: number) => void;
  /** Turns every filter flag off. Pushes a history entry; resets page to 1. */
  clearFlags: () => void;
  /** Clears flags, tags and search back to defaults; keeps the current tab. */
  clearFilters: () => void;
}

function parseTab(value: string | null): ClientListTab {
  return VALID_TABS.includes(value as ClientListTab) ? (value as ClientListTab) : DEFAULT_TAB;
}

function parseFlag(value: string | null): boolean {
  return value === '1';
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

type UrlPatch = Partial<Record<'tab' | 'q' | 'endingSoon' | 'outsidePlan' | 'tags' | 'page', string | null>>;

/**
 * Owns the clients-list page's filter state entirely in the URL — `tab`,
 * `q`, `endingSoon`, `outsidePlan`, `tags`, `page`. Component state holds only what isn't a
 * filter (the raw, undebounced search input, row selection, dialog-open
 * flags). This is the sole source consumed to build the list query key —
 * there is no second copy of this state to drift out of sync with it.
 *
 * Read-side normalisation (`parseTab`/`parsePage`) means a
 * hand-edited or stale URL can never send a bad tab, or outsidePlan with
 * Archived, to the API:
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
    const tab = parseTab(searchParams.get('tab'));
    return {
      tab,
      search: searchParams.get('q') ?? '',
      endingSoon: parseFlag(searchParams.get(FLAG_PARAM.endingSoon)),
      outsidePlan: tab !== 'Archived' && parseFlag(searchParams.get(FLAG_PARAM.outsidePlan)),
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
    (tab: ClientListTab) => {
      // Outside plan cannot be combined with Archived, so switching there drops it.
      update({
        tab: tab === DEFAULT_TAB ? null : tab,
        ...(tab === 'Archived' ? { outsidePlan: null } : {}),
        page: null,
      });
    },
    [update],
  );

  const setFlag = useCallback(
    (flag: ClientListFlag, on: boolean) => {
      update({ [FLAG_PARAM[flag]]: on ? '1' : null, page: null });
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

  const clearFlags = useCallback(() => {
    update({ endingSoon: null, outsidePlan: null, page: null });
  }, [update]);

  const clearFilters = useCallback(() => {
    update({ endingSoon: null, outsidePlan: null, tags: null, q: null, page: null });
  }, [update]);

  return { filters, setSearch, setTab, setFlag, setTagIds, setPage, clearFlags, clearFilters };
}
