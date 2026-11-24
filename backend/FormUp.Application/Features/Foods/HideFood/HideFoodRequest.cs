namespace FormUp.Application.Features.Foods.HideFood;

/// <summary>
/// Request for hiding a food from the caller's library.
/// </summary>
public class HideFoodRequest
{
    /// <summary>Route parameter — public id of the food to hide.</summary>
    public Guid FoodId { get; set; }
}
