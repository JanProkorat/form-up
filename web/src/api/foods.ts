/**
 * Ingredients (backend: Foods) API module.
 *
 * Wraps the NSwag-generated `searchFoodsEndpoint` / `createFoodEndpoint` /
 * `updateFoodEndpoint` / `deleteFoodEndpoint`. Food-tag CRUD/assignment
 * lives in the sibling `food-tags.ts` module (#1120).
 */
import { apiClient } from '@/api/client';
import { postEmptyJson, putEmptyJson } from '@/api/empty-body';
import type {
  UploadFoodImageUrlRequest,
  ConfirmFoodImageRequest,
  FoodSummary,
  FoodCategory,
  FoodOwnerFilter,
  FoodSortField,
  FoodSortDirection,
  CreateFoodRequest,
  UpdateFoodRequest,
} from '@/api/generated';

/** Slot for food image upload: 'main' overwrites hero; 'gallery' appends (max 6). */
export type FoodImageSlot = 'main' | 'gallery';

export interface SearchFoodsParams {
  q?: string;
  /** Matches a food carrying ANY of the supplied categories. Always passed
   * through (even empty) — the generated client serialises it as a repeated
   * `category=` query parameter, same shape as `tags` below. */
  categories: FoodCategory[];
  /** Matches a food the caller has tagged with ANY of the supplied food-tag
   * ids (#1120). Always passed through (even empty) — the generated client
   * serialises it as a repeated `tagIds=` query parameter; hand-building
   * that URL is exactly how the tags filter goes silently wrong. */
  tagIds: string[];
  /** Matches a food whose ownership falls under ANY of the supplied
   * `FoodOwnerFilter` values (#1139). Always passed through (even empty) —
   * same repeated-query-param shape as `categories`/`tagIds` above. */
  owners: FoodOwnerFilter[];
  /** `true` returns ONLY the foods the caller hid; `false` (default) excludes them. */
  hidden: boolean;
  page: number;
  pageSize: number;
  /** Column to sort by. `undefined` means no explicit sort — the backend
   * returns newest-created foods first. */
  sortBy?: FoodSortField;
  /** Direction for `sortBy`. Ignored by the backend when `sortBy` is omitted. */
  sortDir?: FoodSortDirection;
}

export interface SearchFoodsResult {
  foods: FoodSummary[];
  totalCount: number;
  page: number;
  pageSize: number;
}

/** Search foods (ingredients) by name, category and tags, with pagination. */
export async function searchFoods(params: SearchFoodsParams): Promise<SearchFoodsResult> {
  // NSwag reorders array params ahead of scalar ones, and generator output can
  // silently reorder further on regen — always re-check this argument order
  // against the generated `searchFoodsEndpoint` signature, don't assume it.
  const response = await apiClient.searchFoodsEndpoint(
    params.categories,
    params.tagIds,
    params.owners,
    params.hidden,
    params.page,
    params.pageSize,
    params.q,
    params.sortBy,
    params.sortDir,
  );
  return {
    foods: response.foods ?? [],
    totalCount: response.totalCount ?? 0,
    page: response.page ?? params.page,
    pageSize: response.pageSize ?? params.pageSize,
  };
}

/** Get a single food by ID. */
export async function getFood(foodId: string): Promise<FoodSummary> {
  return apiClient.getFoodEndpoint(foodId);
}

/** Create a custom food (Nutritionist only). Defaults to Private visibility. */
export async function createFood(request: CreateFoodRequest): Promise<FoodSummary> {
  return apiClient.createFoodEndpoint(request);
}

/** Update a custom food (owner only). This is a full-state PUT — callers must
 * round-trip every field the backend stores, not just the ones edited in the
 * UI, or the untouched fields get silently wiped. */
export async function updateFood(foodId: string, request: UpdateFoodRequest): Promise<FoodSummary> {
  return apiClient.updateFoodEndpoint(foodId, request);
}

/** Delete a custom food (soft delete, owner only). */
export async function deleteFood(foodId: string): Promise<void> {
  await apiClient.deleteFoodEndpoint(foodId);
}

/** Copies a visible food into the caller's own library (Nutritionist only). The copy is Private. */
export async function copyFood(foodId: string): Promise<void> {
  await postEmptyJson(`/foods/${encodeURIComponent(foodId)}/copy`);
}

/** Hides another coach's / a system food from the caller's lists. Not allowed for the caller's own foods. */
export async function hideFood(foodId: string): Promise<void> {
  await putEmptyJson(`/foods/${encodeURIComponent(foodId)}/hide`);
}

/** Reverses `hideFood`. */
export async function unhideFood(foodId: string): Promise<void> {
  await putEmptyJson(`/foods/${encodeURIComponent(foodId)}/unhide`);
}

/** Get custom foods for the authenticated nutritionist. */
export async function getCustomFoods(params: { page: number; pageSize: number }) {
  const response = await apiClient.getCustomFoodsEndpoint(params.page, params.pageSize);
  return {
    foods: response.foods ?? [],
    totalCount: response.totalCount ?? 0,
    page: response.page ?? params.page,
    pageSize: response.pageSize ?? params.pageSize,
  };
}

/**
 * Request a pre-signed upload URL for a food item image (Nutritionist only).
 * slot=main overwrites the hero image; slot=gallery appends to the gallery (max 6).
 */
export async function requestFoodImageUploadUrl(
  foodId: string,
  slot: FoodImageSlot,
  request: UploadFoodImageUrlRequest,
): Promise<{ uploadUrl: string; blobUrl: string }> {
  const response = await apiClient.uploadFoodImageUrlEndpoint(foodId, slot, request);
  return { uploadUrl: response.uploadUrl ?? '', blobUrl: response.blobUrl ?? '' };
}

/**
 * Confirm a completed food image upload by persisting its blob URL (Nutritionist only).
 * slot=main sets the main imageUrl; slot=gallery appends to galleryImageUrls.
 */
export async function confirmFoodImage(
  foodId: string,
  slot: FoodImageSlot,
  blobUrl: string,
): Promise<void> {
  const body: ConfirmFoodImageRequest = { blobUrl };
  await apiClient.confirmFoodImageEndpoint(foodId, slot, body);
}

/**
 * Clears a food's main picture (owner only). Mirrors the backend's
 * DeleteFoodImageEndpoint — only `ImageUrl` is cleared; the gallery and
 * every other field are left untouched.
 */
export async function removeFoodImage(foodId: string): Promise<void> {
  await apiClient.deleteFoodImageEndpoint(foodId);
}

/** Removes one extra picture from a food's gallery (owner only). Idempotent. */
export async function removeFoodGalleryImage(foodId: string, imageUrl: string): Promise<void> {
  await apiClient.removeFoodGalleryImageEndpoint(foodId, imageUrl);
}

/** Makes a gallery picture the main one; the previous main takes its slot (owner only). */
export async function promoteFoodGalleryImage(foodId: string, imageUrl: string): Promise<void> {
  await apiClient.promoteFoodGalleryImageEndpoint(foodId, { imageUrl });
}
