using FormUp.Application.Infrastructure.Cli;
using FluentAssertions;

namespace FormUp.Tests.Infrastructure.Cli;

/// <summary>
/// Unit tests for <see cref="CliCommandDispatcher.ParseCommand"/> — the pure
/// argument-matching function that replaced the per-flag
/// <c>args.Contains(...)</c> checks. Covers the one behaviour-change vector
/// identified in review: precedence order, position independence, exact
/// element matching, and the empty-args case the
/// <c>WebApplicationFactory&lt;Program&gt;</c> test host boots with.
/// </summary>
public class CliCommandDispatcherTests
{
    [Fact]
    public void ParseCommand_EmptyArgs_ReturnsNone()
    {
        var result = CliCommandDispatcher.ParseCommand([]);

        result.Should().Be(CliCommand.None);
    }

    [Fact]
    public void ParseCommand_NoRecognizedFlag_ReturnsNone()
    {
        var result = CliCommandDispatcher.ParseCommand(["--unrelated-flag"]);

        result.Should().Be(CliCommand.None);
    }

    [Fact]
    public void ParseCommand_SeedFlag_ReturnsSeed()
    {
        var result = CliCommandDispatcher.ParseCommand(["--seed"]);

        result.Should().Be(CliCommand.Seed);
    }

    [Fact]
    public void ParseCommand_QaSeedFlag_ReturnsQaSeed()
    {
        var result = CliCommandDispatcher.ParseCommand(["--qa-seed"]);

        result.Should().Be(CliCommand.QaSeed);
    }

    [Fact]
    public void ParseCommand_WalkSeedFlag_ReturnsWalkSeed()
    {
        var result = CliCommandDispatcher.ParseCommand(["--walk-seed"]);

        result.Should().Be(CliCommand.WalkSeed);
    }

    [Fact]
    public void ParseCommand_BackfillPhotoDescriptionsFlag_ReturnsBackfillPhotoDescriptions()
    {
        var result = CliCommandDispatcher.ParseCommand(["--backfill-photo-descriptions"]);

        result.Should().Be(CliCommand.BackfillPhotoDescriptions);
    }

    [Fact]
    public void ParseCommand_DropLegacyTrainingCollectionsFlag_ReturnsDropLegacyTrainingCollections()
    {
        var result = CliCommandDispatcher.ParseCommand(["--drop-legacy-training-collections"]);

        result.Should().Be(CliCommand.DropLegacyTrainingCollections);
    }

    [Fact]
    public void ParseCommand_RenameTrainerNotesCollectionFlag_ReturnsRenameTrainerNotesCollection()
    {
        var result = CliCommandDispatcher.ParseCommand(["--rename-trainer-notes-collection"]);

        result.Should().Be(CliCommand.RenameTrainerNotesCollection);
    }

    [Fact]
    public void ParseCommand_NoFlag_DoesNotTriggerTheDestructiveDrop()
    {
        // The only destructive command in the seam must never be selected by a boot with
        // no flags (the WebApplicationFactory<Program> test host boots with an empty array)
        // nor by an unrelated flag.
        CliCommandDispatcher.ParseCommand([]).Should().Be(CliCommand.None);
        CliCommandDispatcher.ParseCommand(["--seed"]).Should().Be(CliCommand.Seed);
        CliCommandDispatcher.ParseCommand(["--drop-legacy"]).Should().Be(CliCommand.None);
        CliCommandDispatcher.ParseCommand(["--rename-trainer-notes"]).Should().Be(CliCommand.None);
    }

    [Fact]
    public void ParseCommand_QaSeedFlag_DoesNotTriggerSeed()
    {
        // --qa-seed must NOT be treated as --seed via a substring/prefix match —
        // exact element matching only.
        var result = CliCommandDispatcher.ParseCommand(["--qa-seed"]);

        result.Should().Be(CliCommand.QaSeed);
    }

    [Fact]
    public void ParseCommand_FlagNotFirstElement_StillMatches()
    {
        // Matching is position-independent — args.Contains(...) over the whole array.
        var result = CliCommandDispatcher.ParseCommand(["--verbose", "--drop-legacy-training-collections"]);

        result.Should().Be(CliCommand.DropLegacyTrainingCollections);
    }

    [Fact]
    public void ParseCommand_MultipleFlagsPresent_FirstInFixedOrderWins()
    {
        // Fixed precedence: --seed > --qa-seed > --backfill-photo-descriptions >
        // --drop-legacy-training-collections > --rename-trainer-notes-collection,
        // regardless of array order. This case passes the first four.
        var result = CliCommandDispatcher.ParseCommand(
            ["--drop-legacy-training-collections", "--backfill-photo-descriptions", "--qa-seed", "--seed"]);

        result.Should().Be(CliCommand.Seed);
    }

    [Fact]
    public void ParseCommand_QaSeedAndBackfillFlags_QaSeedWins()
    {
        var result = CliCommandDispatcher.ParseCommand(
            ["--backfill-photo-descriptions", "--qa-seed"]);

        result.Should().Be(CliCommand.QaSeed);
    }
}
