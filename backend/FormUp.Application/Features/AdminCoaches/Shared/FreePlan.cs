namespace FormUp.Application.Features.AdminCoaches.Shared;

/// <summary>
/// The Free plan's names and client cap, read once per request.
/// </summary>
internal class FreePlan
{
    public AdminCoachPlanDto Plan { get; set; } = new();

    public int? MaxActiveClients { get; set; }
}
