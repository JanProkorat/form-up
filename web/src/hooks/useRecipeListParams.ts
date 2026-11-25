import { useCallback, useMemo } from 'react';
import { useSearchParams } from 'react-router-dom';
import {
  DietaryPreference,
  FoodOwnerFilter,
  FoodSortDirection,
  RecipeMealType,
  RecipeSortField,
} from '@/api/recipe-types';

export const RECIPES_PAGE_SIZE = 25;

const VALID_MEAL_TYPES: readonly string[] = Object.values(RecipeMealType);
const VALID_DIETARY_PREFERENCES: readonly string[] = Object.values(DietaryPreference);
const VALID_OWNERS: readonly string[] = Object.values(FoodOwnerFilter);
const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Maps the `sort` URL param's lowercase field slug to its `RecipeSortField`. */
const SORT_FIELD_BY_SLUG: Record<string, RecipeSortField> = {
  name: RecipeSortField.Name,
  calories: RecipeSortField.CaloriesPerServing,
  servings: RecipeSortField.Servings,
  library: RecipeSortField.Library,
};

const SORT_SLUG_BY_FIELD: Partial<Record<RecipeSortField, string>> = {
  [RecipeSortField.Name]: 'name',
  [RecipeSortField.CaloriesPerServing]: 'calories',
  [RecipeSortField.Servings]: 'servings',
  [RecipeSortField.Library]: 'library',
};

export interface RecipeSortState {
  /** `null` means no explicit sort — the server returns newest-created first. */
  sortBy: RecipeSortField | null;
  sortDir: FoodSortDirection;
}

const DEFAULT_SORT: RecipeSortState = { sortBy: null, sortDir: FoodSortDirection.Ascending };

export interface RecipeListFilters extends RecipeSortState {
  search: string;
  mealTypes: RecipeMealType[];
  dietaryPreferences: DietaryPreference[];
  owners: FoodOwnerFilter[];
  /** Ids of the caller's tags; a recipe matches when it carries ANY of them. */
  tags: string[];
  /** Show-hidden view: only the recipes the caller hid (URL `hidden=1`). */
  hidden: boolean;
  page: number;
  pageSize: number;
}

export interface UseRecipeListParamsResult {
  filters: RecipeListFilters;
  /** Sets the search text. Uses `replace` so Back doesn't walk one step per keystroke; resets page to 1. */
  setSearch: (value: string) => void;
  setMealTypes: (mealTypes: RecipeMealType[]) => void;
  setDietaryPreferences: (preferences: DietaryPreference[]) => void;
  setOwners: (owners: FoodOwnerFilter[]) => void;
  /** Toggles the show-hidden view; resets page to 1. */
  setHidden: (hidden: boolean) => void;
  /** Replaces the selected-tags set; resets page to 1. */
  setTags: (tags: string[]) => void;
  /** Cycles a column: unsorted/other column → ascending → descending → cleared. */
  cycleSort: (field: RecipeSortField) => void;
  setPage: (page: number) => void;
  /** Clears search and every filter. Leaves the sort untouched. */
  clearFilters: () => void;
}

function parseEnumList<T extends string>(value: string | null, valid: readonly string[]): T[] {
  if (!value) {
    return [];
  }
  const items = value
    .split(',')
    .map((item) => item.trim())
    .filter((item) => valid.includes(item));
  return Array.from(new Set(items)) as T[];
}

// Non-GUID values (a hand-edited URL) are dropped rather than sent to the
// backend, where they would 400 the whole search.
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

function parsePage(value: string | null): number {
  const parsed = Number(value);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : 1;
}

/** Parses `"<field>-<asc|desc>"`; anything unrecognised falls back to the default. */
function parseSort(value: string | null): RecipeSortState {
  if (!value) {
    return DEFAULT_SORT;
  }
  const [fieldSlug, directionSlug] = value.split('-');
  const field = fieldSlug ? SORT_FIELD_BY_SLUG[fieldSlug] : undefined;
  if (!field || (directionSlug !== 'asc' && directionSlug !== 'desc')) {
    return DEFAULT_SORT;
  }
  return {
    sortBy: field,
    sortDir: directionSlug === 'desc' ? FoodSortDirection.Descending : FoodSortDirection.Ascending,
  };
}

function serializeSort(state: RecipeSortState): string | null {
  if (state.sortBy === null) {
    return null;
  }
  const slug = SORT_SLUG_BY_FIELD[state.sortBy];
  if (!slug) {
    return null;
  }
  return `${slug}-${state.sortDir === FoodSortDirection.Descending ? 'desc' : 'asc'}`;
}

function nextSortState(current: RecipeSortState, field: RecipeSortField): RecipeSortState {
  if (current.sortBy !== field) {
    return { sortBy: field, sortDir: FoodSortDirection.Ascending };
  }
  if (current.sortDir === FoodSortDirection.Ascending) {
    return { sortBy: field, sortDir: FoodSortDirection.Descending };
  }
  return DEFAULT_SORT;
}

type UrlPatch = Partial<Record<'q' | 'mealType' | 'diet' | 'owner' | 'hidden' | 'tags' | 'sort' | 'page', string | null>>;

/**
 * Owns the recipes-list page's filter state entirely in the URL — `q`,
 * `mealType`, `diet`, `owner`, `tags`, `sort`, `page`. Mirrors `useIngredientListParams`.
 */
export function useRecipeListParams(): UseRecipeListParamsResult {
  const [searchParams, setSearchParams] = useSearchParams();

  const filters = useMemo<RecipeListFilters>(() => {
    const sort = parseSort(searchParams.get('sort'));
    return {
      search: searchParams.get('q') ?? '',
      mealTypes: parseEnumList<RecipeMealType>(searchParams.get('mealType'), VALID_MEAL_TYPES),
      dietaryPreferences: parseEnumList<DietaryPreference>(searchParams.get('diet'), VALID_DIETARY_PREFERENCES),
      owners: parseEnumList<FoodOwnerFilter>(searchParams.get('owner'), VALID_OWNERS),
      tags: parseTags(searchParams.get('tags')),
      hidden: searchParams.get('hidden') === '1',
      sortBy: sort.sortBy,
      sortDir: sort.sortDir,
      page: parsePage(searchParams.get('page')),
      pageSize: RECIPES_PAGE_SIZE,
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

  const setMealTypes = useCallback(
    (mealTypes: RecipeMealType[]) => {
      const deduped = Array.from(new Set(mealTypes));
      update({ mealType: deduped.length ? deduped.join(',') : null, page: null });
    },
    [update],
  );

  const setDietaryPreferences = useCallback(
    (preferences: DietaryPreference[]) => {
      const deduped = Array.from(new Set(preferences));
      update({ diet: deduped.length ? deduped.join(',') : null, page: null });
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

  const setTags = useCallback(
    (tags: string[]) => {
      const deduped = Array.from(new Set(tags));
      update({ tags: deduped.length ? deduped.join(',') : null, page: null });
    },
    [update],
  );

  const cycleSort = useCallback(
    (field: RecipeSortField) => {
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
    update({ mealType: null, diet: null, owner: null, hidden: null, tags: null, q: null, page: null });
  }, [update]);

  return {
    filters,
    setSearch,
    setMealTypes,
    setDietaryPreferences,
    setOwners,
    setHidden,
    setTags,
    cycleSort,
    setPage,
    clearFilters,
  };
}
