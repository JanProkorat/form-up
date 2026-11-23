using System.Security.Claims;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Extensions;
using MongoDB.Driver;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Who is calling a shared library (recipes, foods, exercises): a coach with their own items, or an admin-only
/// account that sees and manages the system items alone. Every system-owner filter and marker lives here.
/// </summary>
/// <param name="UserId">The caller's <c>ApplicationUser.Id</c>.</param>
/// <param name="SystemOnly">True for an Admin who holds no coach role.</param>
public readonly record struct LibraryCallerScope(Guid UserId, bool SystemOnly)
{
    /// <summary>Source value stamped on system exercises.</summary>
    public const string SystemSource = "system";

    /// <summary>Builds the scope for the authenticated caller.</summary>
    /// <param name="user">The authenticated caller.</param>
    /// <param name="userId">The caller's user id.</param>
    public static LibraryCallerScope From(ClaimsPrincipal user, Guid userId) => new(userId, user.IsAdminOnly());

    /// <summary>The recipe owner the caller acts as: the system account for an admin-only caller, else the caller.</summary>
    public Guid RecipeOwnerId => SystemOnly ? SystemUsers.AdminId : UserId;

    /// <summary>Whether an admin-only caller must not see a recipe with this owner (it is not a system recipe).</summary>
    /// <param name="ownerId">The recipe's owner.</param>
    public bool HidesRecipe(Guid ownerId) => SystemOnly && ownerId != SystemUsers.AdminId;

    /// <summary>Whether an admin-only caller must not see a food with this owner (system foods have none).</summary>
    /// <param name="ownerId">The food's owner, null for a system food.</param>
    public bool HidesFood(Guid? ownerId) => SystemOnly && ownerId is not null;

    /// <summary>Whether the caller owns a food: an admin-only caller owns the owner-less system foods.</summary>
    /// <param name="ownerId">The food's owner, null for a system food.</param>
    public bool OwnsFood(Guid? ownerId) => SystemOnly ? ownerId is null : ownerId == UserId;

    /// <summary>Matches the foods the caller owns: the owner-less system foods for an admin-only caller.</summary>
    public FilterDefinition<Food> FoodOwnerFilter() =>
        Builders<Food>.Filter.Eq(f => f.NutritionistId, SystemOnly ? null : UserId);

    /// <summary>Whether an admin-only caller must not see this exercise (it is not a system exercise).</summary>
    /// <param name="exercise">The exercise.</param>
    public bool HidesExercise(Exercise exercise) => SystemOnly && exercise.IsCustom;

    /// <summary>Matches only system recipes for an admin-only caller, everything otherwise.</summary>
    public FilterDefinition<Recipe> RecipeFilter() =>
        SystemOnly ? Builders<Recipe>.Filter.Eq(r => r.NutritionistId, SystemUsers.AdminId) : Builders<Recipe>.Filter.Empty;

    /// <summary>Matches only system foods for an admin-only caller, everything otherwise.</summary>
    public FilterDefinition<Food> FoodFilter() =>
        SystemOnly ? Builders<Food>.Filter.Eq(f => f.NutritionistId, null) : Builders<Food>.Filter.Empty;

    /// <summary>Matches only system exercises for an admin-only caller, everything otherwise.</summary>
    public FilterDefinition<Exercise> ExerciseFilter() =>
        SystemOnly ? Builders<Exercise>.Filter.Eq(e => e.IsCustom, false) : Builders<Exercise>.Filter.Empty;
}
