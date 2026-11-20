namespace FormUp.Application.Seed.WalkSeed;

/// <summary>
/// The secrets of one prepared walk account. Only ever written to the gitignored output files, never logged.
/// </summary>
/// <param name="Account">The account.</param>
/// <param name="Password">The password set this run.</param>
/// <param name="RefreshToken">A server-minted refresh token for the Playwright session file.</param>
internal sealed record WalkAccountCredentials(WalkAccount Account, string Password, string RefreshToken);
