import type { Allergen, GetRecipeResponse } from '@/api/recipe-types';
import type { Macros } from '@/lib/recipe-nutrition';

/** One ingredient row in the drawer's Ingredients tab. */
export interface IngredientLine {
  /** Stable React key — a food can appear in the list only once, but the key must survive edits. */
  key: string;
  foodId: string;
  name: string;
  per100: Macros;
  /** `null` for a line loaded from a saved recipe (the server only returns the
   * recipe-level allergen union), the food's own allergens for a freshly picked one. */
  allergens: Allergen[] | null;
  /** Raw input text so a half-typed number isn't clobbered. */
  amountText: string;
  note?: string;
}

/** One preparation step; `id` is stable across reorders (drag-and-drop identity). */
export interface StepItem {
  id: string;
  text: string;
  /** Not editable yet; carried through so saving an existing recipe keeps it. */
  title?: string;
  /** Not editable yet; carried through so saving an existing recipe keeps it. */
  minutes?: number;
}

export function newKey(): string {
  return crypto.randomUUID();
}

export function parseAmount(amountText: string): number {
  return Number(amountText);
}

export function isValidAmount(amountText: string): boolean {
  const amount = parseAmount(amountText);
  return amountText.trim() !== '' && Number.isFinite(amount) && amount > 0;
}

export function linesFromRecipe(recipe: GetRecipeResponse): IngredientLine[] {
  return (recipe.foods ?? []).map((food) => ({
    key: newKey(),
    foodId: food.foodExternalId ?? '',
    name: food.foodName ?? '',
    per100: {
      kcal: food.nutrientValuePer100Grams?.kcal ?? 0,
      protein: food.nutrientValuePer100Grams?.protein ?? 0,
      carbs: food.nutrientValuePer100Grams?.carbs ?? 0,
      fat: food.nutrientValuePer100Grams?.fat ?? 0,
    },
    allergens: null,
    amountText: String(food.amountGrams ?? ''),
    note: food.note,
  }));
}

export function stepsFromRecipe(recipe: GetRecipeResponse): StepItem[] {
  return (recipe.steps ?? []).map((step) => ({
    id: newKey(),
    text: step.description ?? '',
    title: step.title,
    minutes: step.minutes,
  }));
}
