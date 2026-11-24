using FluentAssertions;

namespace FormUp.Tests.Architecture;

/// <summary>
/// Keeps the "newmessage" SignalR event behind <c>ChatMessageNotifier</c>, so a chat message
/// can never reach a recipient without the offline-push decision (#1067).
/// </summary>
public class ChatMessageNotifierArchitectureTests
{
    private const string EventLiteral = "\"newmessage\"";

    [Fact]
    public void NewMessageEventLiteral_AppearsInOneProductionFile_TheNotifier()
    {
        var sourceRoot = FindApplicationSourceRoot();

        var offenders = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(sourceRoot, path))
            .Where(path => File.ReadLines(path).Any(IsCodeLineWithEventLiteral))
            .Select(path => Path.GetFileName(path))
            .Order()
            .ToList();

        offenders.Should().Equal("ChatMessageNotifier.cs");
    }

    private static bool IsCodeLineWithEventLiteral(string line)
    {
        var trimmed = line.TrimStart();
        var isComment = trimmed.StartsWith("//") || trimmed.StartsWith('*') || trimmed.StartsWith("/*");

        return !isComment && trimmed.Contains(EventLiteral, StringComparison.Ordinal);
    }

    private static bool IsBuildOutput(string sourceRoot, string path)
    {
        var relative = Path.GetRelativePath(sourceRoot, path);

        return relative.StartsWith("bin" + Path.DirectorySeparatorChar)
               || relative.StartsWith("obj" + Path.DirectorySeparatorChar);
    }

    private static string FindApplicationSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "FormUp.Application");

            if (File.Exists(Path.Combine(candidate, "FormUp.Application.csproj")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate FormUp.Application above {AppContext.BaseDirectory}");
    }
}
