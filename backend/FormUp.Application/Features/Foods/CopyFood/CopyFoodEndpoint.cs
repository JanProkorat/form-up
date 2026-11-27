using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Foods.GetFood;
using FormUp.Application.Features.Foods.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Foods.CopyFood;

/// <summary>
/// Copies any food the caller can see (their own, or a public one) into a new private food they own.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="imageUpload">Validates the source image URLs.</param>
/// <param name="blobStorage">Blob storage used to duplicate the images.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="logger">Logger.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
internal sealed class CopyFoodEndpoint(
    IMongoContext mongo,
    IImageUploadService imageUpload,
    IBlobStorageService blobStorage,
    TimeProvider timeProvider,
    ILogger<CopyFoodEndpoint> logger) : Endpoint<CopyFoodRequest, FoodSummary>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/foods/{FoodId}/copy");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Copy food to own library";
            s.Description = "Clones a visible food (own, or another coach's or the system's public food) into a new "
                + "private food owned by the caller, with its images duplicated. Tag assignments are not copied.";
            s.Response<FoodSummary>(StatusCodes.Status201Created, "Copy created");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "The subscription does not include ingredients, or the coach role was removed");
            s.Response(StatusCodes.Status404NotFound, "Food not found, deleted, or another coach's private food");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CopyFoodRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var nutritionistId = Guid.Parse(userId);

        if (await this.RespondIfCoachFeatureNotEntitledAsync(
                nutritionistId, entitlements => entitlements.CanUseIngredients,
                "The current subscription does not include ingredients.", ct))
        {
            return;
        }

        var filter = Builders<Food>.Filter.Eq(f => f.ExternalId, req.FoodId)
            & FoodVisibilityFilter.BuildOwnOrPublic(nutritionistId);

        using var cursor = await mongo.Foods.FindAsync(filter, cancellationToken: ct);
        var source = await cursor.FirstOrDefaultAsync(ct);

        if (source is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var copy = new Food
        {
            ExternalId = Guid.NewGuid(),
            Name = source.Name,
            LocalizedNames = source.LocalizedNames is null
                ? null
                : new LocalizedNames { En = source.LocalizedNames.En, Cs = source.LocalizedNames.Cs, De = source.LocalizedNames.De },
            NutrientValue = new NutrientValue
            {
                Kcal = source.NutrientValue.Kcal,
                Protein = source.NutrientValue.Protein,
                Carbs = source.NutrientValue.Carbs,
                Fat = source.NutrientValue.Fat,
                Fiber = source.NutrientValue.Fiber,
                Sugar = source.NutrientValue.Sugar,
                SaturatedFat = source.NutrientValue.SaturatedFat,
                Salt = source.NutrientValue.Salt
            },
            Category = source.Category,
            Allergens = [.. source.Allergens],
            DietaryPreferences = [.. source.DietaryPreferences],
            CommonServings = source.CommonServings
                .Select(serving => new ServingSize { Label = serving.Label, WeightGrams = serving.WeightGrams })
                .ToList(),
            Note = source.Note,
            NutritionistId = nutritionistId,
            Visibility = FoodVisibility.Private,
            IsDeleted = false,
            DateCreated = timeProvider.GetUtcNow().UtcDateTime,
            DateUpdated = null
        };

        var imageCopier = new LibraryImageCopier(imageUpload, blobStorage, logger);

        copy.ImageUrl = await imageCopier.CopyAsync(
            ImageUploadScope.Food, source.ExternalId, copy.ExternalId, FoodImageKeys.MainSlot, source.ImageUrl, ct);
        copy.GalleryImageUrls = await imageCopier.CopyGalleryAsync(
            ImageUploadScope.Food, source.ExternalId, copy.ExternalId, FoodImageKeys.GallerySlot, source.GalleryImageUrls, ct);

        await mongo.Foods.InsertOneAsync(copy, cancellationToken: ct);

        await Send.CreatedAtAsync<GetFoodEndpoint>(
            new { FoodId = copy.ExternalId },
            FoodSummary.FromDocument(copy, currentUserId: nutritionistId),
            cancellation: ct);
    }
}
