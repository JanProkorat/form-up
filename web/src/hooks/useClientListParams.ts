import { useCallback, useMemo } from 'react';
import { useSearchParams } from 'react-router-dom';
import type { ClientListFilter } from '@/api/generated';

/**
 * The clients-list page's tabs. `Active` | `Paused` | `Archived` mirror
 * `ClientListStatus`; `Pending` and `All` are page-only virtual tabs with no
 * backend enum member. `Pending` drives the Pending-tab query instead of the
 * list query; `All` lists Active + Paused clients (no status filter) with the
 * pending rows below them. `OutsidePlan` is virtual too: it lists live clients
 * over the plan's client cap (`outsidePlan=true`, no status).
 */
export type ClientListTab = 'All' | 'Active' | 'Paused' | 'OutsidePlan' | 'Archived' | 'Pending';

const VALID_TABS: readonly ClientListTab[] = ['All', 'Active', 'Paused', 'OutsidePlan', 'Archived', 'Pending'];
const DEFAULT_TAB: ClientListTab = 'All';

// Stale ?chip= values of the removed chips fall back to All.
const VALID_CHIPS: readonly string[] = ['All', 'EndingSoon'];
const DEFAULT_CHIP = 'All' as ClientListFilter;

export const CLIENTS_PAGE_SIZE = 20;

export interface ClientListFilters {
  tab: ClientListTab;
  search: string;
  chip: ClientListFilter;
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
  /** Switches the active filter chip. Pushes a history entry; resets page to 1. */
  setChip: (chip: ClientListFilter) => void;
  /** Replaces the full selected-tag set. Pushes a history entry; resets page to 1. */
  setTagIds: (tagIds: string[]) => void;
  /** Navigates to a page. Pushes a history entry. */
  setPage: (page: number) => void;
  /** Clears chip, tags and search back to defaults; keeps the current tab. */
  clearFilters: () => void;
}

function parseTab(value: string | null): ClientListTab {
  return VALID_TABS.includes(value as ClientListTab) ? (value as ClientListTab) : DEFAULT_TAB;
}

function parseChip(value: string | null): ClientListFilter {
  return VALID_CHIPS.includes(value ?? '') ? (value as ClientListFilter) : DEFAULT_CHIP;
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

type UrlPatch = Partial<Record<'tab' | 'q' | 'chip' | 'tags' | 'page', string | null>>;

/**
 * Owns the clients-list page's filter state entirely in the URL — `tab`,
 * `q`, `chip`, `tags`, `page`. Component state holds only what isn't a
 * filter (the raw, undebounced search input, row selection, dialog-open
 * flags). This is the sole source consumed to build the list query key —
 * there is no second copy of this state to drift out of sync with it.
 *
 * Read-side normalisation (`parseTab`/`parseChip`/`parsePage`) means a
 * hand-edited or stale URL can never send a bad tab/chip to the API:
 * unknown values silently fall back to the default. Unknown tag ids are
 * deliberately NOT dropped here — the backend treats an unrecognised or
 * foreign tag id as matching nothing (see `getClientsEndpoint` doc comment
 * in `generated.ts`), so it is safe to forward as-is; the tag *picker*
 * (phase 3) is the layer that should stop rendering a ghost chip for a
 * tag id no longer present in `useClientTags()`'s result, via `setTagIds`.
 */
export function useClientListParams(): UseClientListParamsResult {
  const [searchParams, setSearchParams] = useSearchParams();

  const filters = useMemo<ClientListFilters>(
    () => ({
      tab: parseTab(searchParams.get('tab')),
      search: searchParams.get('q') ?? '',
      chip: parseChip(searchParams.get('chip')),
      tagIds: parseTagIds(searchParams.get('tags')),
      page: parsePage(searchParams.get('page')),
      pageSize: CLIENTS_PAGE_SIZE,
    }),
    [searchParams],
  );

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
      update({ tab: tab === DEFAULT_TAB ? null : tab, page: null });
    },
    [update],
  );

  const setChip = useCallback(
    (chip: ClientListFilter) => {
      update({ chip: chip === DEFAULT_CHIP ? null : chip, page: null });
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

  const clearFilters = useCallback(() => {
    update({ chip: null, tags: null, q: null, page: null });
  }, [update]);

  return { filters, setSearch, setTab, setChip, setTagIds, setPage, clearFilters };
}
