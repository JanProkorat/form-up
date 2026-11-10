import { SeenMealItemKind } from '@/api/generated';
import type { PlanMeal, SeenMealItemRequest } from '@/api/generated';

/** A seen item with the fields the outbox fills set, as stored on the outbox entry. */
export type SeenItem = SeenMealItemRequest & Required<Pick<SeenMealItemRequest, 'kind' | 'refId' | 'name' | 'amount' | 'kcal'>>;

// The backend rejects anything beyond these with a 400, and a 400 drops the whole queued write.
const MAX_ITEMS = 40;
const MAX_NAME_LENGTH = 200;
const MAX_AMOUNT = 10_000;
const MAX_KCAL = 20_000;

const clamp = (value: number, max: number): number => (Number.isFinite(value) ? Math.min(Math.max(value, 0), max) : 0);

/** The meal's foods and recipes as the client sees them now, cut to what the server accepts. */
export function seenItemsOf(meal: PlanMeal): SeenItem[] {
  const items: SeenItem[] = [];
  for (const food of meal.foods ?? []) {
    if (!food.foodExternalId || !food.foodName?.trim()) continue;
    const grams = food.amountGrams ?? 0;
    items.push({
      kind: SeenMealItemKind.Food,
      refId: food.foodExternalId,
      name: (food.foodName ?? '').slice(0, MAX_NAME_LENGTH),
      amount: clamp(grams, MAX_AMOUNT),
      kcal: Math.round(clamp(((food.nutrientValuePer100Grams?.kcal ?? 0) * grams) / 100, MAX_KCAL)),
    });
  }
  for (const recipe of meal.recipes ?? []) {
    if (!recipe.recipeId || !recipe.recipeName?.trim()) continue;
    const servings = recipe.servings ?? 0;
    items.push({
      kind: SeenMealItemKind.Recipe,
      refId: recipe.recipeId,
      name: (recipe.recipeName ?? '').slice(0, MAX_NAME_LENGTH),
      amount: clamp(servings, MAX_AMOUNT),
      kcal: Math.round(clamp((recipe.nutrientValuePerServing?.kcal ?? 0) * servings, MAX_KCAL)),
    });
  }
  return items.slice(0, MAX_ITEMS);
}
