using FastEndpoints;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Foods.Shared;

namespace FormUp.Application.Features.Foods.SearchFoods;

/// <summary>
/// Request model for searching foods.
/// </summary>
public class SearchFoodsRequest
{
    /// <summary>
    /// Free-text search query.
    /// </summary>
    [BindFrom("q")]
    public string? Query { get; set; }

    /// <summary>
    /// Optional category filter — matches a food carrying ANY of the supplied categories. Bound
    /// from the repeated <c>category</c> query param, so a single <c>?category=Dairy</c> still
    /// binds a one-item list.
    /// </summary>
    [BindFrom("category")]
    public List<FoodCategory> Categories { get; set; } = [];

    /// <summary>
    /// Optional tags filter — matches a food the caller has tagged with ANY of the supplied
    /// <see cref="FoodTagDto.TagId"/> values (#1120). At most 20.
    /// </summary>
    public List<Guid> TagIds { get; set; } = [];

    /// <summary>
    /// Optional owner filter — matches a food whose ownership falls under ANY of the supplied
    /// <see cref="FoodOwnerFilter"/> values. Bound from the repeated <c>owner</c> query param, so
    /// a single <c>?owner=Mine</c> still binds a one-item list. At most 3 (one per enum member).
    /// None supplied applies no owner filter.
    /// </summary>
    [BindFrom("owner")]
    public List<FoodOwnerFilter> Owners { get; set; } = [];

    /// <summary>
    /// When <see langword="true"/>, returns only the foods the caller has hidden instead of the
    /// library without them. Defaults to <see langword="false"/>.
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// Column to sort by. <see langword="null"/> (the default) means no explicit sort —
    /// newest-created foods first.
    /// </summary>
    public FoodSortField? SortBy { get; set; }

    /// <summary>
    /// Direction for <see cref="SortBy"/>. Defaults to <see cref="FoodSortDirection.Ascending"/>
    /// when <see cref="SortBy"/> is set but this is omitted. Ignored when <see cref="SortBy"/> is
    /// <see langword="null"/>.
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
