using FastEndpoints;
using FormUp.Application.Domain.Services;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Features.Exercises.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Exercises.GetExercise;

/// <summary>
/// Retrieves a single exercise by its external ID.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
public class GetExerciseEndpoint(IMongoContext mongo) : Endpoint<GetExerciseRequest, ExerciseDetail>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/exercises/{ExerciseId}");
        Summary(s =>
        {
            s.Summary = "Get exercise by ID";
            s.Description = "Returns a single exercise with full detail by its public identifier.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GetExerciseRequest req, CancellationToken ct)
    {
        var language = HttpContext.Request.Headers.AcceptLanguage.FirstOrDefault()?.Split(',').FirstOrDefault()?.Split('-').FirstOrDefault();

        var filter = Builders<Exercise>.Filter.Eq(e => e.ExternalId, req.ExerciseId)
            & Builders<Exercise>.Filter.Eq(e => e.IsActive, true)
            & LibraryCallerScope.From(User, Guid.Empty).ExerciseFilter();

        using var cursor = await mongo.Exercises.FindAsync(filter, cancellationToken: ct);
        var exercise = await cursor.FirstOrDefaultAsync(ct);

        if (exercise is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(ExerciseDetail.FromDocument(exercise, language), ct);
    }
}
