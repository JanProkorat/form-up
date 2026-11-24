using FastEndpoints;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.Recipes.SearchRecipes;

/// <summary>
/// Request model for searching recipes.
/// </summary>
public class SearchRecipesRequest
{
    /// <summary>
    /// Optional search term matched against recipe name and description.
    /// </summary>
    [BindFrom("search")]
    public string? Search { get; set; }

    /// <summary>
    /// Optional meal type filter — matches a recipe carrying ANY of the supplied meal types. Bound
    /// from the repeated <c>mealType</c> query param.
    /// </summary>
    [BindFrom("mealType")]
    public List<RecipeMealType> MealTypes { get; set; } = [];

    /// <summary>
    /// Optional dietary preference filter — matches a recipe carrying ALL of the supplied
    /// preferences. Bound from the repeated <c>dietaryPreference</c> query param.
    /// </summary>
    [BindFrom("dietaryPreference")]
    public List<DietaryPreference> DietaryPreferences { get; set; } = [];

    /// <summary>
    /// Optional owner filter — matches a recipe whose ownership falls under ANY of the supplied
    /// values (System means the platform catalog). Bound from the repeated <c>owner</c> query param.
    /// </summary>
    [BindFrom("owner")]
    public List<FoodOwnerFilter> Owners { get; set; } = [];

    /// <summary>
    /// Optional tags filter — matches a recipe the caller has tagged with ANY of the supplied tag
    /// ids. At most 20. Bound from the repeated <c>tagId</c> query param.
    /// </summary>
    [BindFrom("tagId")]
    public List<Guid> TagIds { get; set; } = [];

    /// <summary>
    /// When <see langword="true"/>, returns only the recipes the caller has hidden instead of the
    /// library without them. Defaults to <see langword="false"/>.
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// Column to sort by. <see langword="null"/> (the default) means newest-created first.
    /// </summary>
    public RecipeSortField? SortBy { get; set; }

    /// <summary>
    /// Direction for <see cref="SortBy"/>; defaults to ascending when <see cref="SortBy"/> is set.
    /// Ignored when <see cref="SortBy"/> is <see langword="null"/>.
    /// </summary>
    public FoodSortDirection? SortDir { get; set; }

    /// <summary>
    /// Page number (1-based). Defaults to 1.
    /// </summary>
    public int Page { get; set; } = 1;

    /// <summary>
    /// Number of items per page. Defaults to 20.
    /// </summary>
    public int PageSize { get; set; } = 20;
}
