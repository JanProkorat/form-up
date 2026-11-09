namespace FormUp.Application.Features.CoachSubscriptions.SetKeepClients;

/// <summary>
/// The clients a lapsed coach chooses to keep on the Free plan.
/// </summary>
public class SetKeepClientsRequest
{
    /// <summary>Public ids of the clients to keep: unique, and as many as the Free plan's cap allows (or all clients when fewer).</summary>
    public List<Guid> ClientIds { get; set; } = [];
}
