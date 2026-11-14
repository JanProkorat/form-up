using FormUp.Application.Domain.Extensions;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Foods.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Foods.GetCustomFoods;

/// <summary>
/// Retrieves a paginated list of custom foods created by the authenticated nutritionist.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
public class GetCustomFoodsEndpoint(IMongoContext mongo) : Endpoint<GetCustomFoodsRequest, GetCustomFoodsResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/foods/custom");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Get custom foods";
            s.Description = "Returns a paginated list of custom foods created by the authenticated nutritionist.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GetCustomFoodsRequest req, CancellationToken ct)
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
        var filter = Builders<Food>.Filter.Eq(f => f.NutritionistId, nutritionistId)
            & Builders<Food>.Filter.Eq(f => f.IsDeleted, false);

        var totalCount = await mongo.Foods.CountDocumentsAsync(filter, cancellationToken: ct);

        var findOptions = new FindOptions<Food>
        {
            Sort = Builders<Food>.Sort.Descending(f => f.DateCreated),
            Skip = (req.Page - 1) * req.PageSize,
            Limit = req.PageSize
        };

        using var cursor = await mongo.Foods.FindAsync(filter, findOptions, ct);
        var foods = await cursor.ToListAsync(ct);

        var tagsByFoodId = await FoodTagLookup.GetTagsByFoodIdAsync(
            mongo, nutritionistId, foods.Select(f => f.ExternalId).ToList(), ct);

        await Send.OkAsync(new GetCustomFoodsResponse
        {
            Foods = foods
                .Select(f => FoodSummary.FromDocument(
                    f, currentUserId: nutritionistId, tags: tagsByFoodId.GetValueOrDefault(f.ExternalId, [])))
                .ToList(),
            TotalCount = totalCount,
            Page = req.Page,
            PageSize = req.PageSize
        }, ct);
    }
}
