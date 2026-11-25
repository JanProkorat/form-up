namespace FormUp.Application.Features.Foods.UnhideFood;

/// <summary>
/// Request for restoring a hidden food to the caller's library.
/// </summary>
public class UnhideFoodRequest
{
    /// <summary>Route parameter — public id of the food to unhide.</summary>
    public Guid FoodId { get; set; }
}
