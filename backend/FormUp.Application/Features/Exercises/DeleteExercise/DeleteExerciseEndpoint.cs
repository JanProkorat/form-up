using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Services;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Exercises.DeleteExercise;

/// <summary>
/// Soft-deletes a custom exercise. Only the owning trainer can delete.
/// Uses optimistic concurrency — the client must supply the current Version.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Trainer, RemovedCoachRoleMode.Refuse)]
public class DeleteExerciseEndpoint(IMongoContext mongo) : Endpoint<DeleteExerciseRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete("/exercises/{ExerciseId}");
        Roles(AppRoles.Trainer, AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "Delete custom exercise";
            s.Description = "Soft-deletes a custom exercise. Only the trainer who created it can delete. " +
                            "Uses optimistic concurrency via the Version field.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(DeleteExerciseRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var scope = LibraryCallerScope.From(User, Guid.Parse(userId));
        var trainerId = scope.UserId;

        var filter = Builders<Exercise>.Filter.Eq(e => e.ExternalId, req.ExerciseId)
            & Builders<Exercise>.Filter.Eq(e => e.IsActive, true);

        using var cursor = await mongo.Exercises.FindAsync(filter, cancellationToken: ct);
        var exercise = await cursor.FirstOrDefaultAsync(ct);

        if (exercise is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (scope.HidesExercise(exercise))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!scope.SystemOnly && !exercise.IsCustom)
        {
            this.ThrowErrorWithCode(ErrorCodes.SystemExercise, "System exercises cannot be deleted.");
            return;
        }

        if (!scope.SystemOnly && exercise.TrainerId != trainerId)
        {
            this.ThrowErrorWithCode(ErrorCodes.ExerciseNotOwned, "You can only delete your own custom exercises.");
            return;
        }

        // Early optimistic concurrency check (in-memory, before the DB write)
        if (exercise.Version != req.Version)
        {
            await this.SendProblemAsync(409, ErrorCodes.ExerciseVersionConflict,
                "Version conflict. The exercise was modified by another request.", ct);
            return;
        }

        // Version-guarded soft-delete: filter includes current version to prevent concurrent writes.
        //
        // Legacy documents (created before optimistic concurrency was added) have no
        // "version" field stored in BSON. The MongoDB.Driver deserializes them using the
        // C# property initializer (= 1), so clients receive Version = 1. However,
        // Eq(version, 1) does NOT match a field-absent BSON document. To allow the first
        // write (soft-delete) on legacy docs to succeed, the filter also matches when the
        // field is absent and req.Version == 1 (the only value a client gets for a legacy doc).
        var normalVersionMatch = Builders<Exercise>.Filter.Eq(e => e.Version, req.Version);
        var legacyFieldAbsent = req.Version == 1
            ? Builders<Exercise>.Filter.Not(Builders<Exercise>.Filter.Exists(e => e.Version))
            : null;
        var versionClause = legacyFieldAbsent is not null
            ? Builders<Exercise>.Filter.Or(normalVersionMatch, legacyFieldAbsent)
            : normalVersionMatch;

        var versionFilter = Builders<Exercise>.Filter.Eq(e => e.ExternalId, req.ExerciseId)
            & versionClause;

        var update = Builders<Exercise>.Update
            .Set(e => e.IsActive, false)
            .Set(e => e.DateUpdated, DateTime.UtcNow)
            .Set(e => e.Version, exercise.Version + 1);

        var result = await mongo.Exercises.UpdateOneAsync(versionFilter, update, cancellationToken: ct);

        // Double-guard: if ModifiedCount == 0 a concurrent write beat us
        if (result.ModifiedCount == 0)
        {
            await this.SendProblemAsync(409, ErrorCodes.ExerciseVersionConflict,
                "Version conflict. The exercise was modified concurrently.", ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
