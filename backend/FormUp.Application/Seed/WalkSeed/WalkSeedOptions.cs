namespace FormUp.Application.Seed.WalkSeed;

/// <summary>
/// Where the walk seed writes its files and which web origin its session files belong to.
/// </summary>
/// <param name="OutputRoot">Repo root the accounts file and the session folder are resolved from.</param>
/// <param name="WebOrigin">Web origin the Playwright session files are keyed on.</param>
internal sealed record WalkSeedOptions(string OutputRoot, string WebOrigin)
{
    /// <summary>Web origin used when <c>WALK_WEB_ORIGIN</c> is not set.</summary>
    public const string DefaultWebOrigin = "http://localhost:5173";

    /// <summary>Accounts file, relative to <see cref="OutputRoot"/>.</summary>
    public static readonly string AccountsFileRelativePath = Path.Combine(".claude", "private", "walk-accounts.json");

    /// <summary>Folder of the Playwright session files, relative to <see cref="OutputRoot"/>.</summary>
    public static readonly string SessionsFolderRelativePath = Path.Combine(".qa-artifacts", "walk-sessions");

    /// <summary>Absolute path of the accounts file.</summary>
    public string AccountsFilePath => Path.Combine(OutputRoot, AccountsFileRelativePath);

    /// <summary>Absolute path of the session folder.</summary>
    public string SessionsFolderPath => Path.Combine(OutputRoot, SessionsFolderRelativePath);

    /// <summary>
    /// Resolves the options from the environment: <c>WALK_SEED_OUT</c> overrides the repo root, otherwise the root is
    /// the nearest parent of <paramref name="contentRoot"/> holding a <c>.git</c> folder or file (a worktree has a file).
    /// Returns null when no root can be found.
    /// </summary>
    /// <param name="contentRoot">The host content root to walk up from.</param>
    public static WalkSeedOptions? FromEnvironment(string contentRoot)
    {
        var outputRoot = Environment.GetEnvironmentVariable("WALK_SEED_OUT");

        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            outputRoot = FindRepoRoot(contentRoot);
        }

        if (outputRoot is null)
        {
            return null;
        }

        var webOrigin = Environment.GetEnvironmentVariable("WALK_WEB_ORIGIN");

        return new WalkSeedOptions(
            Path.GetFullPath(outputRoot),
            string.IsNullOrWhiteSpace(webOrigin) ? DefaultWebOrigin : webOrigin.TrimEnd('/'));
    }

    private static string? FindRepoRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);

        while (directory is not null)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");

            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
