using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Reads the per-item eaten state of a plan meal off its <see cref="MealLog"/> rows. Shared by the
/// log readers and the item tick endpoints so the "all items eaten" rule lives in one place.
/// A row with an <see cref="MealLog.EatenSnapshot"/> takes its item set from the snapshot; a row
/// without one falls back to the live plan meal.
/// </summary>
internal static class MealItemEatenState
{
    /// <summary>
    /// The ids of every food and recipe of the meal.
    /// </summary>
    public static HashSet<Guid> ItemIds(PlanMeal meal) =>
        meal.Foods.Select(food => food.ItemId)
            .Concat(meal.Recipes.Select(recipe => recipe.ItemId))
            .ToHashSet();

    /// <summary>
    /// The item ids of the row: its snapshot's items, else those of the live meal.
    /// </summary>
    public static HashSet<Guid> ItemIds(MealLog row, PlanMeal? meal) =>
        row.EatenSnapshot is { } snapshot
            ? snapshot.Select(item => item.ItemId).ToHashSet()
            : meal is null ? [] : ItemIds(meal);

    /// <summary>
    /// The item ids of a group of rows of one meal: the first snapshot found, else those of the live meal.
    /// </summary>
    public static HashSet<Guid> ItemIds(IEnumerable<MealLog> rows, PlanMeal? meal)
    {
        var snapshotRow = rows.FirstOrDefault(row => row.EatenSnapshot is not null);

        return snapshotRow is null ? meal is null ? [] : ItemIds(meal) : ItemIds(snapshotRow, meal);
    }

    /// <summary>
    /// The snapshot of the first row of the group that has one, or null.
    /// </summary>
    public static IReadOnlyList<EatenMealItem>? SnapshotOf(IEnumerable<MealLog> rows) =>
        rows.Select(row => row.EatenSnapshot).FirstOrDefault(snapshot => snapshot is not null);

    /// <summary>
    /// The item ids a row counts as eaten. A legacy row (eaten, but written before item ticks
    /// existed) counts as every item of the meal; stored ids no longer among the row's items
    /// (snapshot items, else live meal items) are dropped.
    /// </summary>
    public static HashSet<Guid> EffectiveEatenIds(MealLog row, PlanMeal? meal)
    {
        var items = ItemIds(row, meal);

        if (row.EatenAt is not null && row.EatenItemIds.Count == 0)
        {
            return items;
        }

        var eaten = row.EatenItemIds.ToHashSet();

        if (row.EatenSnapshot is not null || meal is not null)
        {
            eaten.IntersectWith(items);
        }

        return eaten;
    }

    /// <summary>
    /// The eaten state of a meal given the ids counted as eaten and whether a row marks it eaten.
    /// A meal with no items is eaten exactly when a row says so.
    /// </summary>
    public static MealEatenState StateOf(PlanMeal? meal, IReadOnlyCollection<Guid> eatenIds, bool markedEaten) =>
        StateOf(meal is null ? new HashSet<Guid>() : ItemIds(meal), eatenIds, markedEaten);

    /// <summary>
    /// The eaten state given the meal's item ids, the ids counted as eaten and whether a row marks it eaten.
    /// </summary>
    public static MealEatenState StateOf(IReadOnlyCollection<Guid> items, IReadOnlyCollection<Guid> eatenIds, bool markedEaten)
    {
        if (items.Count == 0)
        {
            if (markedEaten)
            {
                return MealEatenState.Eaten;
            }

            return eatenIds.Count > 0 ? MealEatenState.PartlyEaten : MealEatenState.NotEaten;
        }

        var eatenCount = items.Count(eatenIds.Contains);

        if (eatenCount == 0)
        {
            return MealEatenState.NotEaten;
        }

        return eatenCount == items.Count ? MealEatenState.Eaten : MealEatenState.PartlyEaten;
    }

    /// <summary>
    /// The share of the meal eaten, in [0, 1]: eaten current items over current items. A meal with
    /// no items is 1 when a row marks it eaten and 0 otherwise.
    /// </summary>
    public static decimal EatenShare(PlanMeal? meal, IReadOnlyCollection<Guid> eatenIds, bool markedEaten = false) =>
        EatenShare(meal is null ? new HashSet<Guid>() : ItemIds(meal), eatenIds, markedEaten);

    /// <summary>
    /// The share of the given items that are eaten, in [0, 1].
    /// </summary>
    public static decimal EatenShare(IReadOnlyCollection<Guid> items, IReadOnlyCollection<Guid> eatenIds, bool markedEaten)
    {
        if (items.Count == 0)
        {
            return markedEaten ? 1m : 0m;
        }

        return (decimal)items.Count(eatenIds.Contains) / items.Count;
    }

    /// <summary>
    /// Nutrient totals of just the eaten items of the live plan meal.
    /// </summary>
    public static NutrientTotals EatenTotals(
        PlanMeal meal, IReadOnlyCollection<Guid> eatenIds, IMacroCalculatorService macroCalculator) =>
        macroCalculator.CalculateMealTotals(
            meal.Foods.Where(food => eatenIds.Contains(food.ItemId)).ToList(),
            meal.Recipes.Where(recipe => eatenIds.Contains(recipe.ItemId)).ToList());

    /// <summary>
    /// Nutrient totals of the eaten items of a snapshot, rounded like the plan's meal totals; null
    /// when there is no snapshot.
    /// </summary>
    public static NutrientTotals? SnapshotTotals(IReadOnlyList<EatenMealItem>? snapshot, IReadOnlyCollection<Guid> eatenIds)
    {
        if (snapshot is null)
        {
            return null;
        }

        var eaten = snapshot.Where(item => eatenIds.Contains(item.ItemId)).ToList();

        return new NutrientTotals
        {
            Kcal = Math.Round(eaten.Sum(item => item.Kcal), 1),
            Protein = Math.Round(eaten.Sum(item => item.Protein), 1),
            Carbs = Math.Round(eaten.Sum(item => item.Carbs), 1),
            Fat = Math.Round(eaten.Sum(item => item.Fat), 1),
            Fiber = Math.Round(eaten.Sum(item => item.Fibre), 1),
        };
    }

    /// <summary>
    /// Snapshots every food and recipe of the live meal. A food's nutrients are its per-100 g values
    /// scaled by the grams, a recipe's its per-serving values times the servings.
    /// </summary>
    public static List<EatenMealItem> BuildSnapshot(PlanMeal meal)
    {
        var items = meal.Foods.Select(food =>
        {
            var ratio = food.AmountGrams / 100m;

            return new EatenMealItem
            {
                ItemId = food.ItemId,
                Kind = SeenMealItemKind.Food,
                RefId = food.FoodExternalId,
                Name = food.FoodName,
                Amount = food.AmountGrams,
                Kcal = food.NutrientValuePer100Grams.Kcal * ratio,
                Protein = food.NutrientValuePer100Grams.Protein * ratio,
                Carbs = food.NutrientValuePer100Grams.Carbs * ratio,
                Fat = food.NutrientValuePer100Grams.Fat * ratio,
                Fibre = (food.NutrientValuePer100Grams.Fiber ?? 0m) * ratio,
            };
        });

        var recipes = meal.Recipes.Select(recipe => new EatenMealItem
        {
            ItemId = recipe.ItemId,
            Kind = SeenMealItemKind.Recipe,
            RefId = recipe.RecipeId,
            Name = recipe.RecipeName,
            Amount = recipe.Servings,
            Kcal = recipe.NutrientValuePerServing.Kcal * recipe.Servings,
            Protein = recipe.NutrientValuePerServing.Protein * recipe.Servings,
            Carbs = recipe.NutrientValuePerServing.Carbs * recipe.Servings,
            Fat = recipe.NutrientValuePerServing.Fat * recipe.Servings,
            Fibre = (recipe.NutrientValuePerServing.Fiber ?? 0m) * recipe.Servings,
        });

        return items.Concat(recipes).ToList();
    }
}
