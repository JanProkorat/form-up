using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Foods.UpdateFoodTag;

/// <summary>
/// Updates a food tag's name, description, and color. Only the owning nutritionist may update.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class UpdateFoodTagEndpoint(IMongoContext mongo) : Endpoint<UpdateFoodTagRequest, FoodTagDto>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/trainer/food-tags/{TagId}");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Update a food tag";
            s.Description = "Renames, recolors, or redescribes a tag. Only the owning nutritionist may update.";
            s.Responses[StatusCodes.Status200OK] = "Tag updated";
            s.Responses[StatusCodes.Status400BadRequest] = "Invalid request body";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or invalid credentials";
            s.Responses[StatusCodes.Status404NotFound] = "Tag not found, or owned by a different nutritionist";
            s.Responses[StatusCodes.Status409Conflict] = "A different tag with this Name already exists for the caller";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(UpdateFoodTagRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var ownerUserId = Guid.Parse(userId);

        if (await this.RespondIfCoachFeatureNotEntitledAsync(
                ownerUserId, entitlements => entitlements.CanUseIngredients,
                "The current subscription does not include ingredients.", ct))
        {
            return;
        }

        // Single owner-filtered query — a tag owned by a different nutritionist is
        // indistinguishable from one that does not exist at all.
        var tag = await mongo.FoodTags
            .Find(t => t.ExternalId == req.TagId && t.OwnerUserId == ownerUserId)
            .FirstOrDefaultAsync(ct);

        if (tag is null)
        {
            await this.SendProblemAsync(
                StatusCodes.Status404NotFound, ErrorCodes.FoodTagNotFound, "Food tag not found.", ct);
            return;
        }

        var name = req.Name.Trim();
        var normalizedName = name.ToLowerInvariant();

        var update = Builders<FoodTag>.Update
            .Set(t => t.Name, name)
            .Set(t => t.NormalizedName, normalizedName)
            .Set(t => t.Description, req.Description)
            .Set(t => t.ColorHex, req.ColorHex.ToLowerInvariant())
            .Set(t => t.DateUpdated, DateTime.UtcNow)
            .Inc(t => t.Version, 1);

        try
        {
            await mongo.FoodTags.UpdateOneAsync(
                t => t.ExternalId == req.TagId && t.OwnerUserId == ownerUserId,
                update,
                cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // The (OwnerUserId, NormalizedName) unique index caught a name collision with another
            // of the caller's own tags.
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict,
                ErrorCodes.FoodTagNameAlreadyExists,
                "A tag with this name already exists.",
                ct);
            return;
        }

        tag.Name = name;
        tag.NormalizedName = normalizedName;
        tag.Description = req.Description;
        tag.ColorHex = req.ColorHex.ToLowerInvariant();

        await Send.OkAsync(FoodTagDto.FromDocument(tag), ct);
    }
}
