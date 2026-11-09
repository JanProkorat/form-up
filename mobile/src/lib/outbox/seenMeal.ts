import { SeenMealItemKind } from '@/api/generated';
import type { PlanMeal, SeenMealItemRequest } from '@/api/generated';

/** A seen item with every field set, as stored on the outbox entry. */
export type SeenItem = Required<SeenMealItemRequest>;

// The backend rejects anything beyond these with a 400, and a 400 drops the whole queued write.
const MAX_ITEMS = 40;
const MAX_NAME_LENGTH = 200;
const MAX_AMOUNT = 10_000;
const MAX_KCAL = 20_000;
const MAX_MACRO_GRAMS = 5000;

const clamp = (value: number, max: number): number => (Number.isFinite(value) ? Math.min(Math.max(value, 0), max) : 0);

/** The meal's foods and recipes as the client sees them now, cut to what the server accepts. */
export function seenItemsOf(meal: PlanMeal): SeenItem[] {
  const items: SeenItem[] = [];
  for (const food of meal.foods ?? []) {
    if (!food.itemId || !food.foodExternalId || !food.foodName?.trim()) continue;
    const grams = food.amountGrams ?? 0;
    const per100 = food.nutrientValuePer100Grams;
    const macro = (perHundred: number | undefined): number =>
      Math.round(clamp(((perHundred ?? 0) * grams) / 100, MAX_MACRO_GRAMS));
    items.push({
      itemId: food.itemId,
      kind: SeenMealItemKind.Food,
      refId: food.foodExternalId,
      name: (food.foodName ?? '').slice(0, MAX_NAME_LENGTH),
      amount: clamp(grams, MAX_AMOUNT),
      kcal: Math.round(clamp(((per100?.kcal ?? 0) * grams) / 100, MAX_KCAL)),
      protein: macro(per100?.protein),
      carbs: macro(per100?.carbs),
      fat: macro(per100?.fat),
      fibre: macro(per100?.fiber),
    });
  }
  for (const recipe of meal.recipes ?? []) {
    if (!recipe.itemId || !recipe.recipeId || !recipe.recipeName?.trim()) continue;
    const servings = recipe.servings ?? 0;
    const perServing = recipe.nutrientValuePerServing;
    const macro = (each: number | undefined): number => Math.round(clamp((each ?? 0) * servings, MAX_MACRO_GRAMS));
    items.push({
      itemId: recipe.itemId,
      kind: SeenMealItemKind.Recipe,
      refId: recipe.recipeId,
      name: (recipe.recipeName ?? '').slice(0, MAX_NAME_LENGTH),
      amount: clamp(servings, MAX_AMOUNT),
      kcal: Math.round(clamp((perServing?.kcal ?? 0) * servings, MAX_KCAL)),
      protein: macro(perServing?.protein),
      carbs: macro(perServing?.carbs),
      fat: macro(perServing?.fat),
      fibre: macro(perServing?.fiber),
    });
  }
  return items.slice(0, MAX_ITEMS);
}
