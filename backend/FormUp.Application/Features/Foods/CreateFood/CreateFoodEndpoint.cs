using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Foods.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;

namespace FormUp.Application.Features.Foods.CreateFood;

/// <summary>
/// Creates a custom food item owned by the authenticated nutritionist.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class CreateFoodEndpoint(IMongoContext mongo) : Endpoint<CreateFoodRequest, FoodSummary>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/foods");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Create custom food";
            s.Description = "Creates a new custom food item. Only nutritionists can create custom foods.";
            s.Response<FoodSummary>(StatusCodes.Status201Created, "Food created");
            s.Responses[StatusCodes.Status400BadRequest] = "Invalid request body";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or invalid credentials";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CreateFoodRequest req, CancellationToken ct)
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

        var food = new Food
        {
            ExternalId = Guid.NewGuid(),
            Name = req.Name.Trim(),
            LocalizedNames = HasAnyLocalizedName(req) ? new LocalizedNames
            {
                En = req.NameEn?.Trim().NullIfEmpty(),
                Cs = req.NameCs?.Trim().NullIfEmpty(),
                De = req.NameDe?.Trim().NullIfEmpty(),
            } : null,
            NutrientValue = new NutrientValue
            {
                Kcal = req.NutrientValue.Kcal,
                Protein = req.NutrientValue.Protein,
                Carbs = req.NutrientValue.Carbs,
                Fat = req.NutrientValue.Fat,
                Fiber = req.NutrientValue.Fiber,
                Sugar = req.NutrientValue.Sugar,
                SaturatedFat = req.NutrientValue.SaturatedFat,
                Salt = req.NutrientValue.Salt
            },
            Category = req.Category,
            Note = req.Note,
            Allergens = FoodEnumListMapping.ToStoredNames(req.Allergens),
            DietaryPreferences = FoodEnumListMapping.ToStoredNames(req.DietaryPreferences),
            CommonServings = req.CommonServings
                .Select(s => new ServingSize { Label = s.Label, WeightGrams = s.WeightGrams })
                .ToList(),
            NutritionistId = nutritionistId,
            Visibility = req.Visibility,
            DateCreated = DateTime.UtcNow
        };

        await mongo.Foods.InsertOneAsync(food, cancellationToken: ct);

        await HttpContext.Response.SendAsync(
            FoodSummary.FromDocument(food, currentUserId: nutritionistId),
            201,
            cancellation: ct);
    }

    private static bool HasAnyLocalizedName(CreateFoodRequest req) =>
        !string.IsNullOrWhiteSpace(req.NameEn) ||
        !string.IsNullOrWhiteSpace(req.NameCs) ||
        !string.IsNullOrWhiteSpace(req.NameDe);
}
