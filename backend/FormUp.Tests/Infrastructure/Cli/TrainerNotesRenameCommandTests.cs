using FormUp.Application.Domain.Constants;
using FormUp.Application.Infrastructure.Cli;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FormUp.Tests.Infrastructure.Cli;

/// <summary>
/// Integration tests (real MongoDB via <see cref="FitnessApiFactory"/>) for
/// <see cref="CliCommandDispatcher.RenameTrainerNotesCollectionAsync"/> — the #1033 one-shot
/// rename of the legacy snake_case <c>trainer_notes</c> collection to
/// <see cref="MongoCollections.TrainerNotes"/>.
/// </summary>
/// <remarks>
/// Deliberately does NOT use the mock <c>IMongoContext</c> harness — it cannot model
/// <c>ListCollectionNamesAsync</c> or <c>renameCollection</c>, so a mocked test would be
/// unfalsifiable. <see cref="FitnessApiFactory"/>'s database is shared per test class, so each
/// case drops both collection names as its own arrange step.
/// </remarks>
[Collection(TestCollection.Shard3)]
public class TrainerNotesRenameCommandTests(FitnessApiFactory factory)
{
    private const string LegacyName = "trainer_notes";
    private static string TargetName => MongoCollections.TrainerNotes;

    private IMongoDatabase GetDatabase()
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IMongoDatabase>();
    }

    private async Task ResetBothCollectionsAsync(IMongoDatabase database)
    {
        var existing = await (await database.ListCollectionNamesAsync()).ToListAsync();

        if (existing.Contains(LegacyName))
        {
            await database.DropCollectionAsync(LegacyName);
        }

        if (existing.Contains(TargetName))
        {
            await database.DropCollectionAsync(TargetName);
        }
    }

    private static async Task InsertDocumentsAsync(IMongoDatabase database, string collectionName, int count)
    {
        var collection = database.GetCollection<BsonDocument>(collectionName);
        var documents = Enumerable.Range(0, count)
            .Select(_ => new BsonDocument { { "_id", ObjectId.GenerateNewId() } });

        if (count == 0)
        {
            // Mongo lazily creates a collection on first write, so an explicit create is
            // needed to make an empty collection show up in ListCollectionNamesAsync.
            await database.CreateCollectionAsync(collectionName);
            return;
        }

        await collection.InsertManyAsync(documents);
    }

    [Fact]
    public async Task RenameTrainerNotesCollectionAsync_OnlyLegacyExists_RenamesToTarget()
    {
        var database = GetDatabase();
        await ResetBothCollectionsAsync(database);
        await InsertDocumentsAsync(database, LegacyName, 3);

        await CliCommandDispatcher.RenameTrainerNotesCollectionAsync(database);

        var namesAfter = await (await database.ListCollectionNamesAsync()).ToListAsync();
        namesAfter.Should().Contain(TargetName);
        namesAfter.Should().NotContain(LegacyName);

        var renamedCount = await database.GetCollection<BsonDocument>(TargetName)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        renamedCount.Should().Be(3, "the renamed collection must keep every pre-existing document");
    }

    [Fact]
    public async Task RenameTrainerNotesCollectionAsync_OnlyTargetExists_IsNoOp()
    {
        var database = GetDatabase();
        await ResetBothCollectionsAsync(database);
        await InsertDocumentsAsync(database, TargetName, 2);

        await CliCommandDispatcher.RenameTrainerNotesCollectionAsync(database);

        var namesAfter = await (await database.ListCollectionNamesAsync()).ToListAsync();
        namesAfter.Should().Contain(TargetName);
        namesAfter.Should().NotContain(LegacyName);

        var targetCount = await database.GetCollection<BsonDocument>(TargetName)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        targetCount.Should().Be(2, "an already-renamed collection must be left untouched");
    }

    [Fact]
    public async Task RenameTrainerNotesCollectionAsync_NeitherExists_IsNoOp()
    {
        var database = GetDatabase();
        await ResetBothCollectionsAsync(database);

        var act = async () => await CliCommandDispatcher.RenameTrainerNotesCollectionAsync(database);

        await act.Should().NotThrowAsync("Mongo creates collections lazily, so absence of both is a normal, non-error state");

        var namesAfter = await (await database.ListCollectionNamesAsync()).ToListAsync();
        namesAfter.Should().NotContain(LegacyName);
        namesAfter.Should().NotContain(TargetName);
    }

    [Fact]
    public async Task RenameTrainerNotesCollectionAsync_BothExist_LeavesBothUntouchedAndWarns()
    {
        var database = GetDatabase();
        await ResetBothCollectionsAsync(database);
        await InsertDocumentsAsync(database, LegacyName, 5);
        await InsertDocumentsAsync(database, TargetName, 7);

        using var consoleCapture = new ConsoleOutputCapture();

        var act = async () => await CliCommandDispatcher.RenameTrainerNotesCollectionAsync(database);

        await act.Should().NotThrowAsync(
            "a MongoCommandException NamespaceExists (code 48) must never escape — the both-exist case is handled explicitly");

        var namesAfter = await (await database.ListCollectionNamesAsync()).ToListAsync();
        namesAfter.Should().Contain(LegacyName, "the both-exist case must never drop the legacy collection");
        namesAfter.Should().Contain(TargetName, "the both-exist case must never drop the target collection");

        var legacyCount = await database.GetCollection<BsonDocument>(LegacyName)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        var targetCount = await database.GetCollection<BsonDocument>(TargetName)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        legacyCount.Should().Be(5, "the legacy collection's documents must be untouched");
        targetCount.Should().Be(7, "the target collection's documents must be untouched");

        consoleCapture.Output.Should().Contain("WARNING")
            .And.Contain(LegacyName)
            .And.Contain(TargetName)
            .And.Contain("5 document(s)")
            .And.Contain("7 document(s)")
            .And.Contain("merge them manually");
    }
}
