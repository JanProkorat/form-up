using FormUp.Application.Domain.Entities;

namespace FormUp.Application.Seed.WalkSeed;

/// <summary>
/// A prepared walk client with the rows the check-in history hangs on.
/// </summary>
/// <param name="Key">The client's walk key.</param>
/// <param name="User">The client's user.</param>
/// <param name="Profile">The client's profile.</param>
/// <param name="Link">The client's link to the walk coach.</param>
internal sealed record WalkCheckInClient(string Key, ApplicationUser User, ClientProfile Profile, ClientProfessionalLink Link);
