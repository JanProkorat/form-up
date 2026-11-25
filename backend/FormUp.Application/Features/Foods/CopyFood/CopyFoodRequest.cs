namespace FormUp.Application.Features.Foods.CopyFood;

/// <summary>
/// Request for copying a food into the caller's own library.
/// </summary>
public class CopyFoodRequest
{
    /// <summary>Route parameter — public id of the food to copy.</summary>
    public Guid FoodId { get; set; }
}
