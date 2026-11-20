using System.Text.Json;

namespace FormUp.Application.Seed.WalkSeed;

/// <summary>
/// Writes the accounts file and one Playwright storage-state file per account.
/// </summary>
internal static class WalkSeedOutputWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// Writes <c>walk-accounts.json</c> and <c>walk-sessions/&lt;key&gt;.json</c> under the options' root.
    /// </summary>
    /// <param name="options">Output locations and web origin.</param>
    /// <param name="credentials">Every prepared account, admin included.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task WriteAsync(
        WalkSeedOptions options, IReadOnlyList<WalkAccountCredentials> credentials, CancellationToken ct)
    {
        var admin = credentials.Single(entry => entry.Account.Kind == WalkAccountKind.Admin);

        var accountsFile = new
        {
            admin = new { email = admin.Account.Email, password = admin.Password },
            coaches = credentials
                .Where(entry => entry.Account.IsCoach)
                .Select(entry => new
                {
                    key = entry.Account.Key,
                    email = entry.Account.Email,
                    password = entry.Password,
                    note = entry.Account.Note,
                })
                .ToList(),
        };

        await WriteFileAsync(options.AccountsFilePath, JsonSerializer.Serialize(accountsFile, JsonOptions), ct);

        foreach (var entry in credentials)
        {
            var storageState = new
            {
                cookies = Array.Empty<object>(),
                origins = new[]
                {
                    new
                    {
                        origin = options.WebOrigin,
                        localStorage = new[] { new { name = "refreshToken", value = entry.RefreshToken } },
                    },
                },
            };

            await WriteFileAsync(
                Path.Combine(options.SessionsFolderPath, $"{entry.Account.Key}.json"),
                JsonSerializer.Serialize(storageState, JsonOptions),
                ct);
        }
    }

    private static async Task WriteFileAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, ct);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
