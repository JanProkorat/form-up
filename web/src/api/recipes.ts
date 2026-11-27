/**
 * Recipes API module — wraps the NSwag-generated recipe endpoints.
 */
import { apiClient } from '@/api/client';
import { postEmptyJson, putEmptyJson } from '@/api/empty-body';
import type {
  ConfirmRecipeImageRequest,
  CreateRecipeRequest,
  DietaryPreference,
  FoodOwnerFilter,
  FoodSortDirection,
  GetRecipeResponse,
  RecipeMealType,
  RecipeSortField,
  RecipeSummaryDto,
  ReplaceRecipeTagAssignmentsResponse,
  UpdateRecipeRequest,
  UploadRecipeImageUrlRequest,
} from '@/api/generated';

/** Slot for recipe image upload: 'main' overwrites hero; 'gallery' appends (max 6). */
export type RecipeImageSlot = 'main' | 'gallery';

export interface SearchRecipesParams {
  search?: string;
  /** Matches a recipe suited for ANY of the supplied meal types. */
  mealTypes: RecipeMealType[];
  /** Matches a recipe carrying ALL of the supplied dietary preferences. */
  dietaryPreferences: DietaryPreference[];
  /** Matches a recipe whose ownership falls under ANY of the supplied values. */
  owners: FoodOwnerFilter[];
  /** Matches a recipe carrying ANY of the caller's tags with these ids. */
  tagIds: string[];
  /** `true` returns ONLY the recipes the caller hid; `false` (default) excludes them. */
  hidden: boolean;
  page: number;
  pageSize: number;
  /** `undefined` means the server default: newest-created first. */
  sortBy?: RecipeSortField;
  sortDir?: FoodSortDirection;
}

export interface SearchRecipesResult {
  recipes: RecipeSummaryDto[];
  totalCount: number;
  page: number;
  pageSize: number;
}

/** Search recipes by name/description, meal type, dietary preference and owner. */
export async function searchRecipes(params: SearchRecipesParams): Promise<SearchRecipesResult> {
  // NSwag orders array params (mealType, dietaryPreference, owner, tagId)
  // ahead of scalar ones — re-check against the generated signature on regen.
  const response = await apiClient.searchRecipesEndpoint(
    params.mealTypes,
    params.dietaryPreferences,
    params.owners,
    params.tagIds,
    params.hidden,
    params.page,
    params.pageSize,
    params.search,
    params.sortBy,
    params.sortDir,
  );
  return {
    recipes: response.recipes ?? [],
    totalCount: response.totalCount ?? 0,
    page: response.page ?? params.page,
    pageSize: response.pageSize ?? params.pageSize,
  };
}

/** Get a single recipe's full detail. */
export async function getRecipe(recipeId: string): Promise<GetRecipeResponse> {
  return apiClient.getRecipeEndpoint(recipeId);
}

/** Create a recipe (Nutritionist only). Private by default. */
export async function createRecipe(request: CreateRecipeRequest): Promise<GetRecipeResponse> {
  return apiClient.createRecipeEndpoint(request);
}

/** Update a recipe (owner only). Full-state PUT; `version` must echo the loaded value. */
export async function updateRecipe(recipeId: string, request: UpdateRecipeRequest): Promise<GetRecipeResponse> {
  return apiClient.updateRecipeEndpoint(recipeId, request);
}

/** Copies a visible recipe into the caller's own library (Nutritionist only). The copy is Private. */
export async function copyRecipe(recipeId: string): Promise<void> {
  await postEmptyJson(`/recipes/${encodeURIComponent(recipeId)}/copy`);
}

/** Hides another coach's / a system recipe from the caller's lists. Not allowed for the caller's own recipes. */
export async function hideRecipe(recipeId: string): Promise<void> {
  await putEmptyJson(`/recipes/${encodeURIComponent(recipeId)}/hide`);
}

/** Reverses `hideRecipe`. */
export async function unhideRecipe(recipeId: string): Promise<void> {
  await putEmptyJson(`/recipes/${encodeURIComponent(recipeId)}/unhide`);
}

/** Delete a recipe (owner only). */
export async function deleteRecipe(recipeId: string): Promise<void> {
  await apiClient.deleteRecipeEndpoint(recipeId);
}

/** Request a pre-signed upload URL for a recipe image (owner only). */
export async function requestRecipeImageUploadUrl(
  recipeId: string,
  slot: RecipeImageSlot,
  request: UploadRecipeImageUrlRequest,
): Promise<{ uploadUrl: string; blobUrl: string }> {
  const response = await apiClient.uploadRecipeImageUrlEndpoint(recipeId, slot, request);
  return { uploadUrl: response.uploadUrl ?? '', blobUrl: response.blobUrl ?? '' };
}

/** Confirm a completed recipe image upload by persisting its blob URL (owner only). */
export async function confirmRecipeImage(recipeId: string, slot: RecipeImageSlot, blobUrl: string): Promise<void> {
  const body: ConfirmRecipeImageRequest = { blobUrl };
  await apiClient.confirmRecipeImageEndpoint(recipeId, slot, body);
}

/** Clears a recipe's main picture (owner only). */
export async function removeRecipeImage(recipeId: string): Promise<void> {
  await apiClient.deleteRecipeImageEndpoint(recipeId);
}

/** Removes one extra picture from a recipe's gallery (owner only). Idempotent. */
export async function removeRecipeGalleryImage(recipeId: string, imageUrl: string): Promise<void> {
  await apiClient.removeRecipeGalleryImageEndpoint(recipeId, imageUrl);
}

/**
 * Replaces the full set of the caller's own tags assigned to one recipe, via
 * PUT /trainer/recipes/{recipeId}/tags. The recipe may be the caller's own,
 * a system recipe, or another coach's Public recipe.
 */
export async function replaceRecipeTagAssignments(
  recipeId: string,
  tagIds: string[],
): Promise<ReplaceRecipeTagAssignmentsResponse> {
  return apiClient.replaceRecipeTagAssignmentsEndpoint(recipeId, { tagIds });
}

/** Makes a gallery picture the main one; the previous main takes its slot (owner only). */
export async function promoteRecipeGalleryImage(recipeId: string, imageUrl: string): Promise<void> {
  await apiClient.promoteRecipeGalleryImageEndpoint(recipeId, { imageUrl });
}
