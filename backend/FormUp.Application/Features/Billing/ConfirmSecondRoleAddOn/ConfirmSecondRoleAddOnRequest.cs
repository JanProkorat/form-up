namespace FormUp.Application.Features.Billing.ConfirmSecondRoleAddOn;

/// <summary>
/// Request for paying the second-role add-on now.
/// </summary>
public class ConfirmSecondRoleAddOnRequest
{
    /// <summary>
    /// One id per confirm attempt, made by the client when the dialog opens. Repeating a request with the same id never
    /// charges twice; after a declined card the client sends a new id.
    /// </summary>
    public Guid AttemptId { get; set; }
}
