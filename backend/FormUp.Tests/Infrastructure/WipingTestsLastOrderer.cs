using Xunit.Sdk;
using Xunit.v3;

namespace FormUp.Tests.Infrastructure;

/// <summary>
/// Runs the facts that wipe a shared database after the facts that read one shared seed, so the seed is built once
/// instead of again after every wipe. Mark a wiping fact with <see cref="WipesTrait"/>.
/// </summary>
public sealed class WipingTestsLastOrderer : ITestCaseOrderer
{
    /// <summary>Trait name that marks a fact as wiping the shared database.</summary>
    public const string TraitName = "Database";

    /// <summary>Trait value that marks a fact as wiping the shared database.</summary>
    public const string TraitValue = "Wipes";

    /// <inheritdoc />
    public IReadOnlyCollection<TTestCase> OrderTestCases<TTestCase>(IReadOnlyCollection<TTestCase> testCases)
        where TTestCase : notnull, ITestCase =>
        [.. testCases.OrderBy(testCase => Wipes(testCase) ? 1 : 0)];

    private static bool Wipes(ITestCase testCase) =>
        testCase.Traits.TryGetValue(TraitName, out var values) && values.Contains(TraitValue);
}
