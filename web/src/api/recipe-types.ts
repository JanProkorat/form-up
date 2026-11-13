/**
 * Convenience re-exports of the generated recipe-related API types, kept as a
 * sibling module (never edit `generated.ts` directly — it is write-locked, see
 * `rules/code-style.md#generated-files-are-write-locked-if-the-repo-has-one`).
 */
export {
  RecipeVisibility,
  RecipeDifficulty,
  RecipeMealType,
  RecipeSortField,
  DietaryPreference,
  Allergen,
  FoodOwnerFilter,
  FoodSortDirection,
} from './generated';

export type {
  RecipeSummaryDto,
  SearchRecipesResponse,
  GetRecipeResponse,
  CreateRecipeRequest,
  UpdateRecipeRequest,
  RecipeFoodDto,
  RecipeStepDto,
  MealFood,
  NutrientTotals,
  UploadRecipeImageUrlRequest,
} from './generated';
