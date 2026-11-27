import { api } from '@/api/client';
import type { FoodSummary, SearchFoodsResponse } from '@/api/generated';

export const foodQueryKey = (foodId: string) => ['nutrition', 'food', foodId] as const;
export const foodSearchQueryKey = (term: string) => ['nutrition', 'foodSearch', term] as const;

/** Most results the off-plan search shows. */
export const FOOD_SEARCH_PAGE_SIZE = 20;
/** Shortest search term sent to the server. */
export const FOOD_SEARCH_MIN_LENGTH = 2;

/** One food (GET /foods/{foodId}); a client can read foods that are in its plan. */
export function fetchFood(foodId: string, signal?: AbortSignal): Promise<FoodSummary> {
  return api.getFoodEndpoint(foodId, signal);
}

/** Foods matching a name (GET /foods/search), without any category, tag or owner filter. */
export function searchFoods(term: string, signal?: AbortSignal): Promise<SearchFoodsResponse> {
  return api.searchFoodsEndpoint([], [], [], false, 1, FOOD_SEARCH_PAGE_SIZE, term, undefined, undefined, signal);
}

/** The food's name in the app language, falling back to the resolved name. */
export function foodSummaryName(food: FoodSummary, language: string): string {
  const localized = language.startsWith('cs') ? food.nameCs : language.startsWith('de') ? food.nameDe : food.nameEn;
  return localized || food.name || food.rawName || '';
}
