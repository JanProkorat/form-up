using System.Text.RegularExpressions;
using FastEndpoints;
using FormUp.Application.Domain.Services;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Features.Exercises.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FormUp.Application.Features.Exercises.SearchExercises;

/// <summary>
/// Searches exercises by name with optional filters for muscle group, equipment, category, and difficulty.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
public class SearchExercisesEndpoint(IMongoContext mongo) : Endpoint<SearchExercisesRequest, SearchExercisesResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/exercises/search");
        Summary(s =>
        {
            s.Summary = "Search exercises";
            s.Description = "Fulltext search across exercise database with optional filters and pagination.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(SearchExercisesRequest req, CancellationToken ct)
    {
        var language = HttpContext.Request.Headers.AcceptLanguage.FirstOrDefault()?.Split(',').FirstOrDefault()?.Split('-').FirstOrDefault();

        var filterBuilder = Builders<Exercise>.Filter;
        var filter = filterBuilder.Eq(e => e.IsActive, true)
            & LibraryCallerScope.From(User, Guid.Empty).ExerciseFilter();

        if (!string.IsNullOrWhiteSpace(req.Query))
        {
            var escaped = Regex.Escape(req.Query);
            var regex = new BsonRegularExpression(escaped, "i");

            var nameFilters = new List<FilterDefinition<Exercise>>
            {
                filterBuilder.Regex(e => e.Name, regex)
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

        if (req.MuscleGroup.HasValue)
            filter &= filterBuilder.AnyEq(e => e.MuscleGroups, req.MuscleGroup.Value);

        if (req.Equipment.HasValue)
            filter &= filterBuilder.Eq(e => e.Equipment, req.Equipment.Value);

        if (req.Category.HasValue)
            filter &= filterBuilder.Eq(e => e.Category, req.Category.Value);

        if (req.Difficulty.HasValue)
            filter &= filterBuilder.Eq(e => e.Difficulty, req.Difficulty.Value);

        var totalCount = await mongo.Exercises.CountDocumentsAsync(filter, cancellationToken: ct);

        var findOptions = new FindOptions<Exercise>
        {
            Skip = (req.Page - 1) * req.PageSize,
            Limit = req.PageSize
        };

        using var cursor = await mongo.Exercises.FindAsync(filter, findOptions, ct);
        var exercises = await cursor.ToListAsync(ct);

        await Send.OkAsync(new SearchExercisesResponse
        {
            Exercises = exercises.Select(e => ExerciseSummary.FromDocument(e, language)).ToList(),
            TotalCount = totalCount,
            Page = req.Page,
            PageSize = req.PageSize
        }, ct);
    }
}
