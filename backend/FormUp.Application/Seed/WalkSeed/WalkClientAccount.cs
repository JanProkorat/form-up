namespace FormUp.Application.Seed.WalkSeed;

/// <summary>
/// One walk client: a login for the client app, linked to one walk coach.
/// </summary>
/// <param name="Key">Short key; also the user's last name.</param>
/// <param name="Email">Sign-in email.</param>
/// <param name="CoachKey">Key of the coach the client is linked to.</param>
/// <param name="Note">The state in plain English.</param>
internal sealed record WalkClientAccount(string Key, string Email, string CoachKey, string Note);
