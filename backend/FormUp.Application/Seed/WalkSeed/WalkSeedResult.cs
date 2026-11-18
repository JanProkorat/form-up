namespace FormUp.Application.Seed.WalkSeed;

/// <summary>
/// Outcome of a walk seed run.
/// </summary>
/// <param name="Succeeded">False when the run refused to start; nothing was written then.</param>
/// <param name="RefusalReason">Why the run refused, or null.</param>
/// <param name="AccountLines">One line per prepared account (key, email, state). No passwords, no tokens.</param>
internal sealed record WalkSeedResult(bool Succeeded, string? RefusalReason, IReadOnlyList<string> AccountLines)
{
    /// <summary>A run that refused to start.</summary>
    public static WalkSeedResult Refused(string reason) => new(false, reason, []);
}
