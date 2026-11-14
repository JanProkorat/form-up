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

namespace FormUp.Application.Features.Foods.CreateFoodTag;

/// <summary>
/// Creates a new food tag owned by the calling nutritionist.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class CreateFoodTagEndpoint(IMongoContext mongo) : Endpoint<CreateFoodTagRequest, FoodTagDto>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/trainer/food-tags");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Create a food tag";
            s.Description = "Creates a new food tag owned by the calling nutritionist.";
            s.Response<FoodTagDto>(StatusCodes.Status201Created, "Tag created");
            s.Responses[StatusCodes.Status400BadRequest] = "Invalid request body";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or invalid credentials";
            s.Responses[StatusCodes.Status409Conflict] = "A tag with this Name already exists for the caller";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CreateFoodTagRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (await this.RespondIfCoachFeatureNotEntitledAsync(
                Guid.Parse(userId), entitlements => entitlements.CanUseIngredients,
                "The current subscription does not include ingredients.", ct))
        {
            return;
        }

        var name = req.Name.Trim();

        var tag = new FoodTag
        {
            ExternalId = Guid.NewGuid(),
            OwnerUserId = Guid.Parse(userId),
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            Description = req.Description,
            ColorHex = req.ColorHex.ToLowerInvariant(),
            Version = 1,
            DateCreated = DateTime.UtcNow,
        };

        try
        {
            await mongo.FoodTags.InsertOneAsync(tag, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // The (OwnerUserId, NormalizedName) unique index caught a name collision — either a
            // genuine duplicate or a concurrent create for the same name racing this one.
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict,
                ErrorCodes.FoodTagNameAlreadyExists,
                "A tag with this name already exists.",
                ct);
            return;
        }

        await Send.ResponseAsync(FoodTagDto.FromDocument(tag), StatusCodes.Status201Created, ct);
    }
}
