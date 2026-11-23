using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Services;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Exercises.GenerateUploadUrl;

/// <summary>
/// Generates a pre-signed URL for uploading an exercise video directly to blob storage.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="blobStorage">Blob storage service.</param>
[RemovedCoachRole(AppRoles.Trainer, RemovedCoachRoleMode.Refuse)]
public class GenerateUploadUrlEndpoint(
    IMongoContext mongo,
    IBlobStorageService blobStorage) : Endpoint<GenerateUploadUrlRequest, GenerateUploadUrlResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/exercises/{ExerciseId}/upload-url");
        Roles(AppRoles.Trainer, AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "Generate video upload URL";
            s.Description = "Generates a pre-signed URL for direct video upload to blob storage. Only the exercise owner can upload.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GenerateUploadUrlRequest req, CancellationToken ct)
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
            this.ThrowErrorWithCode(ErrorCodes.SystemExercise, "Cannot upload videos for system exercises.");
            return;
        }

        if (!scope.SystemOnly && exercise.TrainerId != trainerId)
        {
            this.ThrowErrorWithCode(ErrorCodes.ExerciseNotOwned, "You can only upload videos for your own exercises.");
            return;
        }

        var extension = req.ContentType switch
        {
            "video/mp4" => "mp4",
            "video/webm" => "webm",
            "video/quicktime" => "mov",
            _ => "mp4"
        };

        var containerPath = $"exercises/videos/{req.ExerciseId}.{extension}";
        var result = await blobStorage.GenerateUploadUrlAsync(
            containerPath,
            req.ContentType,
            TimeSpan.FromMinutes(15),
            ct);

        // Update exercise with the video URL
        var update = Builders<Exercise>.Update
            .Set(e => e.VideoUrl, result.BlobUrl)
            .Set(e => e.DateUpdated, DateTime.UtcNow);

        await mongo.Exercises.UpdateOneAsync(
            e => e.ExternalId == req.ExerciseId,
            update,
            cancellationToken: ct);

        await Send.OkAsync(new GenerateUploadUrlResponse
        {
            UploadUrl = result.UploadUrl,
            VideoUrl = result.BlobUrl
        }, ct);
    }
}
