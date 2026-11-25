import { useCallback, useMemo } from 'react';
import { useSearchParams } from 'react-router-dom';
import { FoodCategory, FoodOwnerFilter, FoodSortDirection, FoodSortField } from '@/api/food-types';

export const INGREDIENTS_PAGE_SIZE = 25;

const VALID_CATEGORIES: readonly string[] = Object.values(FoodCategory);
const VALID_OWNERS: readonly string[] = Object.values(FoodOwnerFilter);
const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Maps the `sort` URL param's lowercase field slug to its `FoodSortField`
 * enum member, and back — keeps the URL readable (`sort=calories-desc`)
 * instead of carrying the PascalCase enum value verbatim. */
const SORT_FIELD_BY_SLUG: Record<string, FoodSortField> = {
  name: FoodSortField.Name,
  calories: FoodSortField.Calories,
  category: FoodSortField.Category,
  library: FoodSortField.Library,
};

// `Record<FoodSortField, string>` must stay exhaustive over every enum
// member, but DateCreated is deliberately not a clickable column (#1139) —
// it's never a parseable slug in SORT_FIELD_BY_SLUG above, so this entry is
// unreachable from the UI and exists purely to satisfy the exhaustiveness
// check.
const SORT_SLUG_BY_FIELD: Record<FoodSortField, string> = {
  [FoodSortField.Name]: 'name',
  [FoodSortField.Calories]: 'calories',
  [FoodSortField.Category]: 'category',
  [FoodSortField.Library]: 'library',
  [FoodSortField.DateCreated]: 'datecreated',
};

/** `sortBy: null` is a distinct, explicit "no sort" state (URL `sort=none`)
 * — not the same as the param being absent, which defaults to Name
 * ascending. See `parseSort`'s doc comment below. */
export interface SortState {
  sortBy: FoodSortField | null;
  sortDir: FoodSortDirection;
}

const DEFAULT_SORT: SortState = { sortBy: FoodSortField.Name, sortDir: FoodSortDirection.Ascending };
const CLEARED_SORT: SortState = { sortBy: null, sortDir: FoodSortDirection.Ascending };

export interface IngredientListFilters {
  search: string;
  categories: FoodCategory[];
  tags: string[];
  owners: FoodOwnerFilter[];
  /** Show-hidden view: only the foods the caller hid (URL `hidden=1`). */
  hidden: boolean;
  sortBy: FoodSortField | null;
  sortDir: FoodSortDirection;
  page: number;
  pageSize: number;
}

export interface UseIngredientListParamsResult {
  filters: IngredientListFilters;
  /** Sets the search text. Uses `replace` (not `push`) so Back doesn't walk
   * one step per keystroke; resets page to 1. */
  setSearch: (value: string) => void;
  /** Replaces the full selected-categories set. Pushes a history entry; resets page to 1. */
  setCategories: (categories: FoodCategory[]) => void;
  /** Replaces the full selected-tags set. Pushes a history entry; resets page to 1. */
  setTags: (tags: string[]) => void;
  /** Replaces the full selected-owners set. Pushes a history entry; resets page to 1. */
  setOwners: (owners: FoodOwnerFilter[]) => void;
  /** Toggles the show-hidden view. Pushes a history entry; resets page to 1. */
  setHidden: (hidden: boolean) => void;
  /** Cycles the given column's sort state: unsorted/other-column → ascending
   * → descending → cleared (back to unsorted). Resets page to 1. */
  cycleSort: (field: FoodSortField) => void;
  /** Navigates to a page. Pushes a history entry. */
  setPage: (page: number) => void;
  /** Clears category, tags, owners and search back to defaults. Deliberately
   * leaves the sort column/direction untouched — clearing filters is not the
   * same action as clearing a sort. */
  clearFilters: () => void;
}

/** Parses the comma-separated `category` URL param, same shape as `tags` —
 * a legacy `?category=Dairy` single-value link still parses to `[Dairy]`,
 * and any value that isn't a known `FoodCategory` is dropped silently. */
function parseCategories(value: string | null): FoodCategory[] {
  if (!value) {
    return [];
  }
  const categories = value
    .split(',')
    .map((category) => category.trim())
    .filter((category) => VALID_CATEGORIES.includes(category));
  return Array.from(new Set(categories)) as FoodCategory[];
}

/** Parses the comma-separated `owner` URL param, same shape/dropped-value
 * handling as `category` above. */
function parseOwners(value: string | null): FoodOwnerFilter[] {
  if (!value) {
    return [];
  }
  const owners = value
    .split(',')
    .map((owner) => owner.trim())
    .filter((owner) => VALID_OWNERS.includes(owner));
  return Array.from(new Set(owners)) as FoodOwnerFilter[];
}

function parsePage(value: string | null): number {
  const parsed = Number(value);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : 1;
}

// A pre-#1120 bookmark could carry a free-text tag value (e.g. `?tags=high-protein`)
// that no longer parses as the food-tag GUIDs this param now holds — dropped
// silently, same as an unknown `category` value above, rather than sent to the
// backend where it 400s the whole search.
function parseTags(value: string | null): string[] {
  if (!value) {
    return [];
  }
  const tags = value
    .split(',')
    .map((tag) => tag.trim())
    .filter((tag) => GUID_PATTERN.test(tag));
  return Array.from(new Set(tags));
}

/**
 * Parses the `sort` URL param. Three distinct states:
 * - absent → `DEFAULT_SORT` (Name ascending) — the page's default view.
 * - `"none"` → `CLEARED_SORT` — the user explicitly cleared the sort; this
 *   must NOT collapse back to the default on reload, so it needs its own
 *   value rather than reusing "absent".
 * - `"<field>-<asc|desc>"` → that field/direction. Anything else
 *   unrecognised (bad slug, bad direction, legacy value) falls back to the
 *   default rather than 400ing the search.
 */
function parseSort(value: string | null): SortState {
  if (!value) {
    return DEFAULT_SORT;
  }
  if (value === 'none') {
    return CLEARED_SORT;
  }
  const [fieldSlug, directionSlug] = value.split('-');
  const field = fieldSlug ? SORT_FIELD_BY_SLUG[fieldSlug] : undefined;
  if (!field || (directionSlug !== 'asc' && directionSlug !== 'desc')) {
    return DEFAULT_SORT;
  }
  return { sortBy: field, sortDir: directionSlug === 'desc' ? FoodSortDirection.Descending : FoodSortDirection.Ascending };
}

/** Serialises a `SortState` back to the `sort` URL param's string form. */
function serializeSort(state: SortState): string {
  if (state.sortBy === null) {
    return 'none';
  }
  const directionSlug = state.sortDir === FoodSortDirection.Descending ? 'desc' : 'asc';
  return `${SORT_SLUG_BY_FIELD[state.sortBy]}-${directionSlug}`;
}

/** Cycles a column's sort state on click: a different (or unset) column
 * starts at ascending; ascending → descending; descending → cleared. */
function nextSortState(current: SortState, field: FoodSortField): SortState {
  if (current.sortBy !== field) {
    return { sortBy: field, sortDir: FoodSortDirection.Ascending };
  }
  if (current.sortDir === FoodSortDirection.Ascending) {
    return { sortBy: field, sortDir: FoodSortDirection.Descending };
  }
  return CLEARED_SORT;
}

type UrlPatch = Partial<Record<'q' | 'category' | 'tags' | 'owner' | 'hidden' | 'sort' | 'page', string | null>>;

/**
 * Owns the ingredients-list page's filter state entirely in the URL — `q`,
 * `category`, `tags`, `owner`, `sort`, `page`. Mirrors `useClientListParams`
 * (`@/hooks/useClientListParams.ts`). Component state holds only what isn't
 * a filter (the raw, undebounced search input, drawer-open state).
 */
export function useIngredientListParams(): UseIngredientListParamsResult {
  const [searchParams, setSearchParams] = useSearchParams();

  const filters = useMemo<IngredientListFilters>(() => {
    const sort = parseSort(searchParams.get('sort'));
    return {
      search: searchParams.get('q') ?? '',
      categories: parseCategories(searchParams.get('category')),
      tags: parseTags(searchParams.get('tags')),
      owners: parseOwners(searchParams.get('owner')),
      hidden: searchParams.get('hidden') === '1',
      sortBy: sort.sortBy,
      sortDir: sort.sortDir,
      page: parsePage(searchParams.get('page')),
      pageSize: INGREDIENTS_PAGE_SIZE,
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

  const setCategories = useCallback(
    (categories: FoodCategory[]) => {
      const deduped = Array.from(new Set(categories));
      update({ category: deduped.length ? deduped.join(',') : null, page: null });
    },
    [update],
  );

  const setTags = useCallback(
    (tags: string[]) => {
      const deduped = Array.from(new Set(tags));
      update({ tags: deduped.length ? deduped.join(',') : null, page: null });
    },
    [update],
  );

  const setOwners = useCallback(
    (owners: FoodOwnerFilter[]) => {
      const deduped = Array.from(new Set(owners));
      update({ owner: deduped.length ? deduped.join(',') : null, page: null });
    },
    [update],
  );

  const setHidden = useCallback(
    (hidden: boolean) => {
      update({ hidden: hidden ? '1' : null, page: null });
    },
    [update],
  );

  const cycleSort = useCallback(
    (field: FoodSortField) => {
      // Read the live URL: React Router re-renders in a transition, so rendered
      // params can lag the URL during rapid clicks.
      const current = parseSort(new URLSearchParams(window.location.search).get('sort'));
      const next = nextSortState(current, field);
      update({ sort: serializeSort(next), page: null });
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
    update({ category: null, tags: null, owner: null, hidden: null, q: null, page: null });
  }, [update]);

  return { filters, setSearch, setCategories, setTags, setOwners, setHidden, cycleSort, setPage, clearFilters };
}
