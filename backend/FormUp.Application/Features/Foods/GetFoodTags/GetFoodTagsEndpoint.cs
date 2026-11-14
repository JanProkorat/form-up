using FormUp.Application.Domain.Extensions;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Foods.GetFoodTags;

/// <summary>
/// Lists the coach-private food tags owned by the calling nutritionist (#1120).
/// </summary>
/// <param name="mongo">MongoDB context.</param>
public class GetFoodTagsEndpoint(IMongoContext mongo) : EndpointWithoutRequest<GetFoodTagsResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/trainer/food-tags");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "List food tags";
            s.Description = "Returns every food tag owned by the calling nutritionist. Powers the "
                + "Ingredients tags filter popover and the drawer's tag chips.";
            s.Responses[StatusCodes.Status200OK] = "The caller's food tags";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or invalid credentials";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
    {
        var userIdClaim = User.FindFirstValue(AppClaims.UserId);

        if (!Guid.TryParse(userIdClaim, out var ownerUserId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (await this.RespondIfCoachFeatureNotEntitledAsync(
                ownerUserId, entitlements => entitlements.CanUseIngredients,
                "The current subscription does not include ingredients.", ct))
        {
            return;
        }

        var tags = await mongo.FoodTags
            .Find(t => t.OwnerUserId == ownerUserId)
            .ToListAsync(ct);

        var sortedTags = tags
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(FoodTagDto.FromDocument)
            .ToList();

        await Send.OkAsync(new GetFoodTagsResponse { Tags = sortedTags }, ct);
    }
}
