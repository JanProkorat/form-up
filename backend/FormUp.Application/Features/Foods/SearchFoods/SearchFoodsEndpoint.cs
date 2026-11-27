using System.Security.Claims;
using System.Text.RegularExpressions;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Foods.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FormUp.Application.Features.Foods.SearchFoods;

/// <summary>
/// Searches foods by name in the local database.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
public class SearchFoodsEndpoint(
    IMongoContext mongo) : Endpoint<SearchFoodsRequest, SearchFoodsResponse>
{
    /// <summary>
    /// The field name the computed sort key is written to via an <c>$addFields</c> pipeline
    /// stage. Never part of the response — <see cref="Food"/> is annotated
    /// <c>[BsonIgnoreExtraElements]</c>, so deserializing the aggregate's output silently drops
    /// it.
    /// </summary>
    private const string SortKeyField = "sortKey";

    /// <inheritdoc />
    public override void Configure()
    {
        Get("/foods/search");
        Summary(s =>
        {
            s.Summary = "Search foods";
            s.Description = "Fulltext search across food database with optional multi-value category, tags, and " +
                "owner filters, sortable columns, and pagination. With no sort, results are name ascending " +
                "(localized to the caller's language) — callers wanting newest-first (e.g. the trainer portal's " +
                "cleared-sort state) request SortBy=DateCreated, SortDir=Descending explicitly. Foods the caller " +
                "hid are left out; Hidden=true returns only those.";
            s.Response<SearchFoodsResponse>(StatusCodes.Status200OK, "Matching foods");
            s.Responses[StatusCodes.Status400BadRequest] =
                "Invalid page, page size, tags filter, category filter, owner filter, or sort field/direction";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or invalid credentials";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(SearchFoodsRequest req, CancellationToken ct)
    {
        var language = HttpContext.Request.Headers.AcceptLanguage.FirstOrDefault()?.Split(',').FirstOrDefault()?.Split('-').FirstOrDefault();

        var userIdClaim = User.FindFirstValue(AppClaims.UserId);
        if (!Guid.TryParse(userIdClaim, out var currentUserId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var filterBuilder = Builders<Food>.Filter;
        var scope = LibraryCallerScope.From(User, currentUserId);
        var filter = FoodVisibilityFilter.BuildOwnOrPublic(currentUserId) & scope.FoodFilter();

        // Typed Guid filters only: GUIDs are stored as binary UUIDs, so a string filter would match nothing.
        // Not(AnyEq) also matches legacy documents that have no hiddenByNutritionistIds field.
        var hiddenByCaller = filterBuilder.AnyEq(f => f.HiddenByNutritionistIds, currentUserId);
        filter &= req.Hidden ? hiddenByCaller : filterBuilder.Not(hiddenByCaller);

        if (req.Categories.Count > 0)
        {
            // "Match any" semantics — same shape as the tags filter below.
            filter &= filterBuilder.In(f => f.Category, req.Categories);
        }

        var ownerFilter = BuildOwnerFilter(req.Owners, currentUserId);
        if (ownerFilter is not null)
        {
            filter &= ownerFilter;
        }

        if (req.TagIds.Count > 0)
        {
            // Two-step lookup: resolve which foods the caller has tagged with any of the
            // requested tags first, then narrow the food query by external id. A caller with no
            // matching assignment gets zero food ids — respond with an empty page immediately
            // rather than let an empty In(...) fall through to "no filter at all".
            var taggedFoodIds = await FoodTagLookup.FindFoodIdsWithAnyTagAsync(mongo, currentUserId, req.TagIds, ct);

            if (taggedFoodIds.Count == 0)
            {
                await Send.OkAsync(new SearchFoodsResponse
                {
                    Foods = [],
                    TotalCount = 0,
                    Page = req.Page,
                    PageSize = req.PageSize
                }, ct);
                return;
            }

            filter &= filterBuilder.In(f => f.ExternalId, taggedFoodIds);
        }

        if (!string.IsNullOrWhiteSpace(req.Query))
        {
            var escaped = Regex.Escape(req.Query);
            var regex = new BsonRegularExpression(escaped, "i");

            // Match against canonical name and the localized name for the user's language
            var nameFilters = new List<FilterDefinition<Food>>
            {
                filterBuilder.Regex(f => f.Name, regex)
            };

            var localizedField = language?.ToLowerInvariant() switch
            {
                "en" => "localizedNames.en",
                "cs" => "localizedNames.cs",
                "de" => "localizedNames.de",
                _ => null
            };

            if (localizedField is not null)
            {
                nameFilters.Add(filterBuilder.Regex(localizedField, regex));
            }

            filter &= filterBuilder.Or(nameFilters);
        }

        var totalCount = await mongo.Foods.CountDocumentsAsync(filter, cancellationToken: ct);

        // Whitelisted to cs/en/de before it's used to build any Mongo field path or collation
        // locale — never pass an arbitrary Accept-Language token straight into either.
        var sortLanguage = language?.ToLowerInvariant() is "cs" or "en" or "de" ? language.ToLowerInvariant() : null;

        var localFoods = await FetchSortedPageAsync(mongo, filter, req, currentUserId, sortLanguage, ct);

        var tagsByFoodId = await FoodTagLookup.GetTagsByFoodIdAsync(
            mongo, currentUserId, localFoods.Select(f => f.ExternalId).ToList(), ct);

        await Send.OkAsync(new SearchFoodsResponse
        {
            Foods = localFoods
                .Select(f => FoodSummary.FromDocument(
                    f, language, currentUserId, tagsByFoodId.GetValueOrDefault(f.ExternalId, [])))
                .ToList(),
            TotalCount = totalCount,
            Page = req.Page,
            PageSize = req.PageSize
        }, ct);
    }

    /// <summary>
    /// Builds the "caller matches ANY of the requested owner buckets" filter — <see
    /// langword="null"/> when no owner values were supplied, so the caller can skip ANDing it in.
    /// </summary>
    private static FilterDefinition<Food>? BuildOwnerFilter(List<FoodOwnerFilter> owners, Guid currentUserId)
    {
        if (owners.Count == 0)
        {
            return null;
        }

        var filterBuilder = Builders<Food>.Filter;
        var ownerFilters = new List<FilterDefinition<Food>>();

        if (owners.Contains(FoodOwnerFilter.Mine))
        {
            ownerFilters.Add(filterBuilder.Eq(f => f.NutritionistId, currentUserId));
        }

        if (owners.Contains(FoodOwnerFilter.System))
        {
            ownerFilters.Add(filterBuilder.Eq(f => f.NutritionistId, null));
        }

        if (owners.Contains(FoodOwnerFilter.OtherCoaches))
        {
            // The outer own-or-public filter already restricts every candidate to Public-or-mine,
            // so "not null and not mine" is equivalent to "another coach's Public food" here —
            // re-checking Visibility would be redundant.
            ownerFilters.Add(
                filterBuilder.Ne(f => f.NutritionistId, null) & filterBuilder.Ne(f => f.NutritionistId, currentUserId));
        }

        return filterBuilder.Or(ownerFilters);
    }

    /// <summary>
    /// Runs the match filter through an aggregation pipeline, adding a computed <see
    /// cref="SortKeyField"/> only when sorting by something other than <see
    /// cref="FoodSortField.DateCreated"/>, sorting, then paging. A <see langword="null"/> <see
    /// cref="SearchFoodsRequest.SortBy"/> is treated exactly as an explicit <see
    /// cref="FoodSortField.Name"/> / <see cref="FoodSortDirection.Ascending"/> request — future
    /// callers (e.g. a plan food picker) get alphabetical results by default.
    /// </summary>
    private static async Task<List<Food>> FetchSortedPageAsync(
        IMongoContext mongo,
        FilterDefinition<Food> filter,
        SearchFoodsRequest req,
        Guid currentUserId,
        string? sortLanguage,
        CancellationToken ct)
    {
        var sortBuilder = Builders<Food>.Sort;

        // A null SortBy is not "no sort" — it's an implicit Name/Ascending request, so the
        // trainer portal's "cleared sort" UI state must instead ask for DateCreated/Descending
        // explicitly to get newest-first (maintainer decision, 2026-10-01).
        var effectiveSortBy = req.SortBy ?? FoodSortField.Name;
        var effectiveSortDir = req.SortBy is null ? FoodSortDirection.Ascending : req.SortDir ?? FoodSortDirection.Ascending;

        SortDefinition<Food> sort;
        BsonValue? sortKeyExpression = null;
        Collation? collation = null;

        if (effectiveSortBy == FoodSortField.DateCreated)
        {
            // dateCreated sorts directly off the stored field — no computed sort key or
            // collation needed. _id breaks ties in the same direction.
            sort = effectiveSortDir == FoodSortDirection.Descending
                ? sortBuilder.Combine(sortBuilder.Descending(f => f.DateCreated), sortBuilder.Descending(f => f.Id))
                : sortBuilder.Combine(sortBuilder.Ascending(f => f.DateCreated), sortBuilder.Ascending(f => f.Id));
        }
        else
        {
            var primarySort = effectiveSortDir == FoodSortDirection.Descending
                ? sortBuilder.Descending(SortKeyField)
                : sortBuilder.Ascending(SortKeyField);
            var tieBreaker = effectiveSortDir == FoodSortDirection.Descending
                ? sortBuilder.Descending(f => f.Id)
                : sortBuilder.Ascending(f => f.Id);
            sort = sortBuilder.Combine(primarySort, tieBreaker);

            sortKeyExpression = BuildSortKeyExpression(effectiveSortBy, sortLanguage, currentUserId);

            // Collation applies to Name and Category — both sort on a translated string in the
            // caller's language. A non-simple collation makes the $match stage's string
            // equalities (category, visibility) collation-aware too, which both changes their
            // semantics and stops simple-collation indexes being used for them, but that's
            // accepted here the same way it already was for Name.
            collation = effectiveSortBy is FoodSortField.Name or FoodSortField.Category
                ? new Collation(sortLanguage ?? "cs")
                : null;
        }

        var aggregateOptions = new AggregateOptions();
        if (collation is not null)
        {
            aggregateOptions.Collation = collation;
        }

        var aggregate = mongo.Foods.Aggregate(aggregateOptions).Match(filter);

        if (sortKeyExpression is not null)
        {
            PipelineStageDefinition<Food, Food> addFieldsStage =
                new BsonDocument("$addFields", new BsonDocument(SortKeyField, sortKeyExpression));
            aggregate = aggregate.AppendStage(addFieldsStage);
        }

        aggregate = aggregate
            .Sort(sort)
            .Skip((req.Page - 1) * req.PageSize)
            .Limit(req.PageSize);

        return await aggregate.ToListAsync(ct);
    }

    /// <summary>
    /// Builds the per-<see cref="FoodSortField"/> aggregation expression used as the computed
    /// <see cref="SortKeyField"/>.
    /// </summary>
    private static BsonValue BuildSortKeyExpression(FoodSortField sortBy, string? sortLanguage, Guid currentUserId) =>
        sortBy switch
        {
            // Mirrors LocalizedNames.Resolve (preferred ?? en ?? cs ?? de), then
            // FoodSummary.FromDocument's further fallback to the canonical name.
            FoodSortField.Name => new BsonDocument("$ifNull", new BsonArray
            {
                $"$localizedNames.{sortLanguage ?? "en"}",
                "$localizedNames.en",
                "$localizedNames.cs",
                "$localizedNames.de",
                "$name"
            }),

            // nutrientValue.kcal has no explicit BSON representation, so it may be stored as a
            // string or a Decimal128 depending on the driver's default conversion — $toDouble
            // coerces either to a real number so the sort is numeric, not lexical.
            FoodSortField.Calories => new BsonDocument("$toDouble", "$nutrientValue.kcal"),

            // category is stored as its enum member name (BsonRepresentation.String) — map it to
            // the translated label for the caller's language (FoodCategoryLabels, mirrored from
            // the web locale files) and sort alphabetically on that, with the Collation above
            // applying the matching language rules.
            FoodSortField.Category => BuildCategoryLabelSwitch(sortLanguage),

            // Mine (0) before Other coaches (1) before System/no-owner (2).
            FoodSortField.Library => new BsonDocument("$switch", new BsonDocument
            {
                ["branches"] = new BsonArray
                {
                    new BsonDocument
                    {
                        ["case"] = new BsonDocument(
                            "$eq", new BsonArray { "$nutritionistId", new BsonBinaryData(currentUserId, GuidRepresentation.Standard) }),
                        ["then"] = 0
                    },
                    new BsonDocument
                    {
                        ["case"] = new BsonDocument("$eq", new BsonArray { "$nutritionistId", BsonNull.Value }),
                        ["then"] = 2
                    }
                },
                ["default"] = 1
            }),

            _ => throw new ArgumentOutOfRangeException(nameof(sortBy), sortBy, "Unhandled FoodSortField.")
        };

    /// <summary>
    /// Builds a <c>$switch</c> mapping each stored <see cref="FoodCategory"/> member name to its
    /// translated label (<see cref="FoodCategoryLabels.ForLanguage"/>), so the Category sort key
    /// is alphabetical by label rather than by enum member name.
    /// </summary>
    private static BsonValue BuildCategoryLabelSwitch(string? sortLanguage)
    {
        var labels = FoodCategoryLabels.ForLanguage(sortLanguage);

        var branches = new BsonArray(labels.Select(entry => new BsonDocument
        {
            ["case"] = new BsonDocument("$eq", new BsonArray { "$category", entry.Key.ToString() }),
            ["then"] = entry.Value
        }));

        return new BsonDocument("$switch", new BsonDocument
        {
            ["branches"] = branches,
            ["default"] = "$category"
        });
    }
}
