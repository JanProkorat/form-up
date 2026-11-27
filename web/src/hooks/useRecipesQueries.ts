import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  searchRecipes,
  getRecipe,
  createRecipe,
  updateRecipe,
  deleteRecipe,
  requestRecipeImageUploadUrl,
  confirmRecipeImage,
  removeRecipeImage,
  removeRecipeGalleryImage,
  promoteRecipeGalleryImage,
  replaceRecipeTagAssignments,
  copyRecipe,
  hideRecipe,
  unhideRecipe,
} from '@/api/recipes';
import { searchFoods } from '@/api/foods';
import type { CreateRecipeRequest, UpdateRecipeRequest, UploadRecipeImageUrlRequest } from '@/api/recipe-types';
import { getErrorStatus, showApiError, showSuccess } from '@/lib/api-errors';
import { bumpImageVersion } from '@/hooks/useImageVersion';
import { uploadPictureFile } from '@/components/library/pictureUpload';
import { FoodSortDirection, FoodSortField } from '@/api/food-types';
import type { RecipeListFilters } from '@/hooks/useRecipeListParams';

export function recipeImageCacheKey(recipeId: string): string {
  return `recipe:${recipeId}`;
}

/** A 403 is a role gate (trainer-only coach), not a transient failure — never retried. */
function retryUnlessForbidden(failureCount: number, error: unknown): boolean {
  return getErrorStatus(error) !== 403 && failureCount < 1;
}

/** The recipes list, filtered/sorted/paginated per `filters`. */
export function useRecipes(filters: RecipeListFilters, enabled: boolean) {
  const mealTypesKey = [...filters.mealTypes].sort().join(',');
  const dietKey = [...filters.dietaryPreferences].sort().join(',');
  const ownersKey = [...filters.owners].sort().join(',');
  const tagsKey = [...filters.tags].sort().join(',');

  return useQuery({
    queryKey: [
      'recipes',
      'list',
      {
        search: filters.search,
        mealTypesKey,
        dietKey,
        ownersKey,
        tagsKey,
        hidden: filters.hidden,
        sortBy: filters.sortBy,
        sortDir: filters.sortDir,
        page: filters.page,
        pageSize: filters.pageSize,
      },
    ],
    queryFn: () =>
      searchRecipes({
        search: filters.search || undefined,
        mealTypes: filters.mealTypes,
        dietaryPreferences: filters.dietaryPreferences,
        owners: filters.owners,
        tagIds: filters.tags,
        hidden: filters.hidden,
        page: filters.page,
        pageSize: filters.pageSize,
        sortBy: filters.sortBy ?? undefined,
        sortDir: filters.sortBy ? filters.sortDir : undefined,
      }),
    placeholderData: keepPreviousData,
    enabled,
    retry: retryUnlessForbidden,
  });
}

/** One recipe's full detail — loaded when the drawer opens an existing row. */
export function useRecipe(recipeId: string | undefined) {
  return useQuery({
    queryKey: ['recipes', 'detail', recipeId],
    queryFn: () => getRecipe(recipeId as string),
    enabled: Boolean(recipeId),
    // The drawer edits a snapshot, so a background refetch must not swap it
    // out from under the form; the drawer re-reads on each open instead.
    staleTime: 0,
    gcTime: 0,
  });
}

/** A recipe's detail for the library info popover: fetched only while it is open, never retried on 404. */
export function useRecipeInfo(recipeId: string | undefined, open: boolean) {
  return useQuery({
    queryKey: ['recipes', 'info', recipeId],
    queryFn: () => getRecipe(recipeId as string),
    enabled: open && Boolean(recipeId),
    retry: (failureCount, error) => getErrorStatus(error) !== 404 && retryUnlessForbidden(failureCount, error),
  });
}

/** Ingredient (food) search backing the recipe drawer's Ingredients tab — own + public + system foods. */
export function useIngredientSearch(term: string) {
  return useQuery({
    queryKey: ['recipes', 'foodSearch', term],
    queryFn: () =>
      searchFoods({
        q: term,
        categories: [],
        tagIds: [],
        owners: [],
        hidden: false,
        page: 1,
        pageSize: 8,
        sortBy: FoodSortField.Name,
        sortDir: FoodSortDirection.Ascending,
      }),
    enabled: term.trim().length > 0,
    placeholderData: keepPreviousData,
  });
}

/** Creates a recipe. The drawer shows the success/error toasts via this hook. */
export function useCreateRecipe() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: CreateRecipeRequest) => createRecipe(request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['recipes'] });
      showSuccess('recipes.drawer.createSuccess');
    },
    onError: (error) => {
      showApiError(error, 'recipes.drawer.createError');
    },
  });
}

export interface UpdateRecipeVariables {
  recipeId: string;
  request: UpdateRecipeRequest;
}

/** Updates a recipe (owner only). Full-state PUT carrying the loaded `version`. */
export function useUpdateRecipe() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (variables: UpdateRecipeVariables) => updateRecipe(variables.recipeId, variables.request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['recipes'] });
      showSuccess('recipes.drawer.updateSuccess');
    },
    onError: (error) => {
      showApiError(error, 'recipes.drawer.updateError');
    },
  });
}

/** Copies a visible recipe into the caller's own library. The new private copy shows up in the list. */
export function useCopyRecipe() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (recipeId: string) => copyRecipe(recipeId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['recipes'] });
      showSuccess('recipes.copySuccess');
    },
    onError: (error) => {
      showApiError(error, 'recipes.copyError');
    },
  });
}

/** Hides a recipe the caller does not own from their lists. */
export function useHideRecipe() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (recipeId: string) => hideRecipe(recipeId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['recipes'] });
      showSuccess('recipes.hideSuccess');
    },
    onError: (error) => {
      showApiError(error, 'recipes.hideError');
    },
  });
}

/** Brings a hidden recipe back into the caller's lists. */
export function useUnhideRecipe() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (recipeId: string) => unhideRecipe(recipeId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['recipes'] });
      showSuccess('recipes.unhideSuccess');
    },
    onError: (error) => {
      showApiError(error, 'recipes.unhideError');
    },
  });
}

/** Deletes a recipe (owner only). */
export function useDeleteRecipe() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (recipeId: string) => deleteRecipe(recipeId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['recipes'] });
      showSuccess('recipes.deleteSuccess');
    },
    onError: (error) => {
      showApiError(error, 'recipes.deleteError');
    },
  });
}

/** Requests a pre-signed upload URL for a recipe's main picture (owner only). */
export function useRequestRecipeImageUploadUrl() {
  return useMutation({
    mutationFn: (variables: { recipeId: string; request: UploadRecipeImageUrlRequest }) =>
      requestRecipeImageUploadUrl(variables.recipeId, 'main', variables.request),
  });
}

/** Confirms a completed main-picture upload; bumps the cache-buster and refreshes lists. */
export function useConfirmRecipeImage() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (variables: { recipeId: string; blobUrl: string }) =>
      confirmRecipeImage(variables.recipeId, 'main', variables.blobUrl),
    onSuccess: (_data, variables) => {
      bumpImageVersion(queryClient, recipeImageCacheKey(variables.recipeId));
      queryClient.invalidateQueries({ queryKey: ['recipes'] });
    },
  });
}

/** Removes a recipe's main picture (owner only). */
export function useRemoveRecipeImage() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (recipeId: string) => removeRecipeImage(recipeId),
    onSuccess: (_data, recipeId) => {
      bumpImageVersion(queryClient, recipeImageCacheKey(recipeId));
      queryClient.invalidateQueries({ queryKey: ['recipes'] });
      showSuccess('library.picture.removeSuccess');
    },
    onError: (error) => {
      showApiError(error, 'library.picture.removeError');
    },
  });
}

/** Requests a pre-signed upload URL for an extra (gallery) picture. */
export function useRequestRecipeGalleryUploadUrl() {
  return useMutation({
    mutationFn: (variables: { recipeId: string; request: UploadRecipeImageUrlRequest }) =>
      requestRecipeImageUploadUrl(variables.recipeId, 'gallery', variables.request),
  });
}

/** Confirms a gallery upload. Picture changes never bump the recipe version, so only the caches refresh. */
export function useConfirmRecipeGalleryImage() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (variables: { recipeId: string; blobUrl: string }) =>
      confirmRecipeImage(variables.recipeId, 'gallery', variables.blobUrl),
    // A rejected confirm (full gallery, stale list) refetches so the grid shows the server's truth.
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['recipes'] });
    },
  });
}

/** Removes one extra picture by its stored URL. */
export function useRemoveRecipeGalleryImage() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (variables: { recipeId: string; imageUrl: string }) =>
      removeRecipeGalleryImage(variables.recipeId, variables.imageUrl),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['recipes'] });
      showSuccess('library.picture.removeSuccess');
    },
    onError: (error) => {
      showApiError(error, 'library.picture.removeError');
    },
  });
}

/**
 * Promotes an extra picture to main (swap). Any failure refetches the recipe, so
 * a RECIPE_VERSION_CONFLICT or a vanished picture leaves the tab showing the
 * server's current state next to the readable message.
 */
export function usePromoteRecipeGalleryImage() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (variables: { recipeId: string; imageUrl: string }) =>
      promoteRecipeGalleryImage(variables.recipeId, variables.imageUrl),
    onSuccess: (_data, variables) => {
      bumpImageVersion(queryClient, recipeImageCacheKey(variables.recipeId));
      queryClient.invalidateQueries({ queryKey: ['recipes'] });
    },
    onError: (error) => {
      showApiError(error, 'recipes.pictures.promoteError');
      queryClient.invalidateQueries({ queryKey: ['recipes'] });
    },
  });
}

/** Uploaders for a freshly created recipe's staged pictures: main first, then gallery, each upload-url, PUT, confirm. */
export function useRecipeStagedUploads() {
  const requestMain = useRequestRecipeImageUploadUrl();
  const confirmMain = useConfirmRecipeImage();
  const requestGallery = useRequestRecipeGalleryUploadUrl();
  const confirmGallery = useConfirmRecipeGalleryImage();
  return {
    uploadMain: (recipeId: string, file: File) =>
      uploadPictureFile(
        file,
        (request) => requestMain.mutateAsync({ recipeId, request }),
        async (blobUrl) => {
          await confirmMain.mutateAsync({ recipeId, blobUrl });
        },
      ),
    uploadGallery: (recipeId: string, file: File) =>
      uploadPictureFile(
        file,
        (request) => requestGallery.mutateAsync({ recipeId, request }),
        async (blobUrl) => {
          await confirmGallery.mutateAsync({ recipeId, blobUrl });
        },
      ),
  };
}

export interface ReplaceRecipeTagAssignmentsVariables {
  recipeId: string;
  tagIds: string[];
}

/**
 * Replaces one recipe's tag assignments for the caller. Not optimistic: the
 * response is the source of truth for what got assigned, and an unknown tag
 * (404 FOOD_TAG_NOT_FOUND) is shown through its localized `apiErrors` message.
 */
export function useReplaceRecipeTagAssignments() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (variables: ReplaceRecipeTagAssignmentsVariables) =>
      replaceRecipeTagAssignments(variables.recipeId, variables.tagIds),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['recipes'] });
    },
    onError: (error) => {
      showApiError(error, 'recipes.tags.assignError');
    },
  });
}
