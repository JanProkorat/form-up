namespace FormUp.Application.Seed.WalkSeed;

/// <summary>
/// The password of one prepared walk client. Only ever written to the gitignored accounts file, never logged.
/// </summary>
/// <param name="Client">The client.</param>
/// <param name="Password">The password set this run.</param>
internal sealed record WalkClientCredentials(WalkClientAccount Client, string Password);
