import { keepPreviousData, useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import {
  searchFoods,
  createFood,
  updateFood,
  deleteFood,
  requestFoodImageUploadUrl,
  confirmFoodImage,
  removeFoodImage,
  removeFoodGalleryImage,
  promoteFoodGalleryImage,
  getFood,
  copyFood,
  hideFood,
  unhideFood,
} from '@/api/foods';
import {
  getFoodTags,
  createFoodTag,
  updateFoodTag,
  deleteFoodTag,
  replaceFoodTagAssignments,
  type CreateFoodTagRequest,
  type UpdateFoodTagRequest,
} from '@/api/food-tags';
import { FoodSortDirection, FoodSortField } from '@/api/food-types';
import type { CreateFoodRequest, FoodSummary, UpdateFoodRequest } from '@/api/food-types';
import type { UploadFoodImageUrlRequest } from '@/api/generated';
import { uploadPictureFile } from '@/components/library/pictureUpload';
import { getErrorCode, showApiError, showSuccess } from '@/lib/api-errors';
import type { IngredientListFilters } from '@/hooks/useIngredientListParams';
import { bumpImageVersion } from '@/hooks/useImageVersion';

/**
 * The ingredients (foods) list, filtered/paginated per `filters`. Mirrors
 * `useClients` (`@/hooks/useClientsQueries.ts`) — `tagsKey` is a stable,
 * sorted primitive so re-picking the same tags in a different click order
 * hits one cache entry instead of two. `filters.tags` holds food-tag ids
 * (#1120), despite the field's name (kept for the URL param — see
 * `useIngredientListParams`).
 */
export function useIngredients(filters: IngredientListFilters) {
  const tagsKey = [...filters.tags].sort().join(',');
  const categoriesKey = [...filters.categories].sort().join(',');
  const ownersKey = [...filters.owners].sort().join(',');

  return useQuery({
    queryKey: [
      'ingredients',
      'list',
      {
        search: filters.search,
        categoriesKey,
        tagsKey,
        ownersKey,
        hidden: filters.hidden,
        sortBy: filters.sortBy,
        sortDir: filters.sortDir,
        page: filters.page,
        pageSize: filters.pageSize,
      },
    ],
    queryFn: () =>
      searchFoods({
        q: filters.search || undefined,
        categories: filters.categories,
        tagIds: filters.tags,
        owners: filters.owners,
        hidden: filters.hidden,
        page: filters.page,
        pageSize: filters.pageSize,
        // A cleared sort (`sort=none` in the URL, filters.sortBy === null)
        // must still request a deterministic order — newest first — rather
        // than fall back to the backend's own no-sortBy default (Name
        // ascending). This is a wire-request mapping only: the URL keeps
        // `sort=none` (see useIngredientListParams' CLEARED_SORT), and
        // DateCreated never becomes a clickable column header.
        sortBy: filters.sortBy ?? FoodSortField.DateCreated,
        sortDir: filters.sortBy ? filters.sortDir : FoodSortDirection.Descending,
      }),
    placeholderData: keepPreviousData,
  });
}

/** The caller's own food tags (#1120) — powers the Tags filter popover, the
 * drawer's tag picker, and the table's per-row chips. Nutritionist-only;
 * a trainer-only coach's caller never mounts this (see `IngredientsPage`). */
export function useFoodTags() {
  return useQuery({
    queryKey: ['foodTags'],
    queryFn: getFoodTags,
  });
}

/**
 * Creates a food tag. No optimistic insert — the server mints the id and
 * enforces per-owner name uniqueness (FOOD_TAG_NAME_ALREADY_EXISTS), which
 * is a real error the user must see, not something to paper over locally.
 */
export function useCreateFoodTag() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: CreateFoodTagRequest) => createFoodTag(request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['foodTags'] });
    },
    onError: (error) => {
      showApiError(error, 'ingredients.tags.createError');
    },
  });
}

export interface UpdateFoodTagVariables {
  tagId: string;
  request: UpdateFoodTagRequest;
}

/** Renames/recolors/redescribes a food tag. Invalidates `['ingredients']` too
 * — the tag's name/color is denormalised onto every tagged food's `tags` chip. */
export function useUpdateFoodTag() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (variables: UpdateFoodTagVariables) => updateFoodTag(variables.tagId, variables.request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['foodTags'] });
      queryClient.invalidateQueries({ queryKey: ['ingredients'] });
      queryClient.invalidateQueries({ queryKey: ['recipes'] });
    },
    onError: (error) => {
      showApiError(error, 'ingredients.tags.updateError');
    },
  });
}

export function useDeleteFoodTag() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (tagId: string) => deleteFoodTag(tagId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['foodTags'] });
      queryClient.invalidateQueries({ queryKey: ['ingredients'] });
      queryClient.invalidateQueries({ queryKey: ['recipes'] });
    },
    onError: (error) => {
      showApiError(error, 'ingredients.tags.deleteError');
    },
  });
}

export interface ReplaceFoodTagAssignmentsVariables {
  foodId: string;
  tagIds: string[];
}

/** Replaces one food's tag assignments for the caller. Not optimistic —
 * mirrors `useAssignClientTags`' own reasoning (the mutation response is the
 * source of truth for what actually got assigned). */
export function useReplaceFoodTagAssignments() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (variables: ReplaceFoodTagAssignmentsVariables) =>
      replaceFoodTagAssignments(variables.foodId, variables.tagIds),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['ingredients'] });
    },
    onError: (error) => {
      showApiError(error, 'ingredients.tags.assignError');
    },
  });
}

/** Creates a coach-owned food (Nutritionist only). Defaults to Private visibility. */
export function useCreateFood() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: CreateFoodRequest) => createFood(request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['ingredients'] });
      showSuccess('ingredients.drawer.createSuccess');
    },
    onError: (error) => {
      // KCAL_INCONSISTENT is shown inline under the Calories field by the
      // drawer's own per-call onError (IngredientDrawer.tsx) — toasting it
      // here too would show the same message twice. Every other error
      // still gets the toast.
      if (getErrorCode(error) === 'KCAL_INCONSISTENT') {
        return;
      }
      showApiError(error, 'ingredients.drawer.createError');
    },
  });
}

export interface UpdateFoodVariables {
  foodId: string;
  request: UpdateFoodRequest;
}

/** Updates a coach-owned food (owner only). Full-state PUT — see `updateFood`'s doc comment. */
export function useUpdateFood() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (variables: UpdateFoodVariables) => updateFood(variables.foodId, variables.request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['ingredients'] });
      showSuccess('ingredients.drawer.updateSuccess');
    },
    onError: (error) => {
      // Same reasoning as useCreateFood's onError above — the drawer's own
      // per-call onError already shows KCAL_INCONSISTENT inline.
      if (getErrorCode(error) === 'KCAL_INCONSISTENT') {
        return;
      }
      showApiError(error, 'ingredients.drawer.updateError');
    },
  });
}

/** Copies a visible food into the caller's own library. The new private copy shows up in the list. */
export function useCopyFood() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (foodId: string) => copyFood(foodId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['ingredients'] });
      showSuccess('ingredients.copySuccess');
    },
    onError: (error) => {
      showApiError(error, 'ingredients.copyError');
    },
  });
}

/** Hides a food the caller does not own from their lists. */
export function useHideFood() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (foodId: string) => hideFood(foodId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['ingredients'] });
      showSuccess('ingredients.hideSuccess');
    },
    onError: (error) => {
      showApiError(error, 'ingredients.hideError');
    },
  });
}

/** Brings a hidden food back into the caller's lists. */
export function useUnhideFood() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (foodId: string) => unhideFood(foodId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['ingredients'] });
      showSuccess('ingredients.unhideSuccess');
    },
    onError: (error) => {
      showApiError(error, 'ingredients.unhideError');
    },
  });
}

/** Soft-deletes a coach-owned food (owner only). */
export function useDeleteFood() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (foodId: string) => deleteFood(foodId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['ingredients'] });
      showSuccess('ingredients.deleteSuccess');
    },
    onError: (error) => {
      showApiError(error, 'ingredients.deleteError');
    },
  });
}

/**
 * Per-food cache-buster for the picture `<img src>` (#1140). Every upload now
 * gets a unique blob key, so it only matters for legacy fixed-key images
 * (`foods/{id}.jpg`) that a replace could leave stale. This is not server data:
 * a plain counter living in its own query-cache entry (deliberately NOT
 * namespaced under `['ingredients']`, so the broad
 * `invalidateQueries({ queryKey: ['ingredients'] })` calls below never
 * touch it), bumped only after a confirmed upload/replace/remove. Both the
 * drawer and the table read the same counter for the same food, so a
 * replace is visible in both places at once.
 */
export function foodImageCacheKey(foodId: string): string {
  return `food:${foodId}`;
}

function bumpFoodImageVersion(queryClient: QueryClient, foodId: string): void {
  bumpImageVersion(queryClient, foodImageCacheKey(foodId));
}

/** Requests a pre-signed upload URL for a food's main picture (owner only).
 * The caller PUTs the file to `uploadUrl` directly (never through this
 * mutation) and only then confirms via `useConfirmFoodImage`. */
export function useRequestFoodImageUploadUrl() {
  return useMutation({
    mutationFn: (variables: { foodId: string; request: UploadFoodImageUrlRequest }) =>
      requestFoodImageUploadUrl(variables.foodId, 'main', variables.request),
  });
}

export interface ConfirmFoodImageVariables {
  foodId: string;
  blobUrl: string;
}

/**
 * Confirms a completed main-picture upload. Call only after the browser's
 * own PUT to the pre-signed URL has already succeeded — see
 * `PictureField`'s upload flow. Bumps the cache-buster and
 * invalidates the ingredients list/detail queries so the new picture shows
 * up everywhere.
 */
export function useConfirmFoodImage() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (variables: ConfirmFoodImageVariables) =>
      confirmFoodImage(variables.foodId, 'main', variables.blobUrl),
    onSuccess: (_data, variables) => {
      bumpFoodImageVersion(queryClient, variables.foodId);
      queryClient.invalidateQueries({ queryKey: ['ingredients'] });
    },
  });
}

/** Requests a pre-signed upload URL for an extra (gallery) picture. */
export function useRequestFoodGalleryUploadUrl() {
  return useMutation({
    mutationFn: (variables: { foodId: string; request: UploadFoodImageUrlRequest }) =>
      requestFoodImageUploadUrl(variables.foodId, 'gallery', variables.request),
  });
}

/** Confirms a gallery upload. A rejected confirm (full gallery, stale list) refetches so the grid shows the server's truth. */
export function useConfirmFoodGalleryImage() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (variables: ConfirmFoodImageVariables) =>
      confirmFoodImage(variables.foodId, 'gallery', variables.blobUrl),
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['ingredients'] });
    },
  });
}

/** Removes one extra picture by its stored URL. */
export function useRemoveFoodGalleryImage() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (variables: { foodId: string; imageUrl: string }) =>
      removeFoodGalleryImage(variables.foodId, variables.imageUrl),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['ingredients'] });
      showSuccess('library.picture.removeSuccess');
    },
    onError: (error) => {
      showApiError(error, 'library.picture.removeError');
    },
  });
}

/**
 * Promotes an extra picture to main (swap). Any failure refetches the food, so a
 * vanished picture or a concurrent change leaves the tab showing the server's
 * current state next to the readable message.
 */
export function usePromoteFoodGalleryImage() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (variables: { foodId: string; imageUrl: string }) =>
      promoteFoodGalleryImage(variables.foodId, variables.imageUrl),
    onSuccess: (_data, variables) => {
      bumpFoodImageVersion(queryClient, variables.foodId);
      queryClient.invalidateQueries({ queryKey: ['ingredients'] });
    },
    onError: (error) => {
      showApiError(error, 'ingredients.pictures.promoteError');
      queryClient.invalidateQueries({ queryKey: ['ingredients'] });
    },
  });
}

/** Uploaders for a freshly created food's staged pictures: main first, then gallery, each upload-url, PUT, confirm. */
export function useFoodStagedUploads() {
  const requestMain = useRequestFoodImageUploadUrl();
  const confirmMain = useConfirmFoodImage();
  const requestGallery = useRequestFoodGalleryUploadUrl();
  const confirmGallery = useConfirmFoodGalleryImage();
  return {
    uploadMain: (foodId: string, file: File) =>
      uploadPictureFile(
        file,
        (request) => requestMain.mutateAsync({ foodId, request }),
        async (blobUrl) => {
          await confirmMain.mutateAsync({ foodId, blobUrl });
        },
      ),
    uploadGallery: (foodId: string, file: File) =>
      uploadPictureFile(
        file,
        (request) => requestGallery.mutateAsync({ foodId, request }),
        async (blobUrl) => {
          await confirmGallery.mutateAsync({ foodId, blobUrl });
        },
      ),
  };
}

/**
 * One food's full detail, for the drawer's Pictures tab. The list row the
 * drawer opened from seeds it as placeholder data; the broad `['ingredients']`
 * invalidations after each picture action refetch it.
 */
export function useFood(foodId: string | undefined, snapshot: FoodSummary | undefined) {
  return useQuery({
    queryKey: ['ingredients', 'detail', foodId],
    queryFn: () => getFood(foodId as string),
    enabled: Boolean(foodId),
    placeholderData: snapshot,
  });
}

/** Removes a food's main picture (owner only), after an explicit confirm step. */
export function useRemoveFoodImage() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (foodId: string) => removeFoodImage(foodId),
    onSuccess: (_data, foodId) => {
      bumpFoodImageVersion(queryClient, foodId);
      queryClient.invalidateQueries({ queryKey: ['ingredients'] });
      showSuccess('library.picture.removeSuccess');
    },
    onError: (error) => {
      showApiError(error, 'library.picture.removeError');
    },
  });
}
