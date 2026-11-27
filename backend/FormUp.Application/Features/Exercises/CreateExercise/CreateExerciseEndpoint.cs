using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Services;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Features.Exercises.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;

namespace FormUp.Application.Features.Exercises.CreateExercise;

/// <summary>
/// Creates a custom exercise owned by the authenticated trainer.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Trainer, RemovedCoachRoleMode.Refuse)]
public class CreateExerciseEndpoint(IMongoContext mongo) : Endpoint<CreateExerciseRequest, ExerciseSummary>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/exercises");
        Roles(AppRoles.Trainer, AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "Create custom exercise";
            s.Description = "Creates a new custom exercise. Only trainers can create custom exercises.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CreateExerciseRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var scope = LibraryCallerScope.From(User, Guid.Parse(userId));

        var exercise = new Exercise
        {
            ExternalId = Guid.NewGuid(),
            Name = req.Name.Trim(),
            LocalizedNames = HasAnyLocalizedName(req) ? new LocalizedNames
            {
                En = req.NameEn?.Trim(),
                Cs = req.NameCs?.Trim(),
                De = req.NameDe?.Trim(),
            } : null,
            Description = req.Description?.Trim(),
            MuscleGroups = req.MuscleGroups,
            Equipment = req.Equipment,
            Category = req.Category,
            Difficulty = req.Difficulty,
            TechniqueNotes = req.TechniqueNotes?.Trim(),
            IsCustom = !scope.SystemOnly,
            TrainerId = scope.SystemOnly ? null : scope.UserId,
            IsActive = true,
            Source = scope.SystemOnly ? LibraryCallerScope.SystemSource : "custom",
            DateCreated = DateTime.UtcNow,
            Version = 1
        };

        await mongo.Exercises.InsertOneAsync(exercise, cancellationToken: ct);

        await HttpContext.Response.SendAsync(ExerciseSummary.FromDocument(exercise), 201, cancellation: ct);
    }

    private static bool HasAnyLocalizedName(CreateExerciseRequest req) =>
        !string.IsNullOrWhiteSpace(req.NameEn) ||
        !string.IsNullOrWhiteSpace(req.NameCs) ||
        !string.IsNullOrWhiteSpace(req.NameDe);
}
