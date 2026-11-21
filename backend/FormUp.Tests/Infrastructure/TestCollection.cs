namespace FormUp.Tests.Infrastructure;

/// <summary>
/// Shared integration collection: every class in it shares one <see cref="FitnessApiFactory"/>
/// (one host, one Postgres database, one Mongo database). New integration classes use
/// <c>[Collection(TestCollection.Name)]</c>.
/// </summary>
[CollectionDefinition(Name)]
public class TestCollection : ICollectionFixture<FitnessApiFactory>
{
    public const string Name = "Integration";
    public const string Shard2 = "Integration2";
    public const string Shard3 = "Integration3";
    public const string Shard4 = "Integration4";
}

/// <summary>
/// Second integration shard. A collection runs its classes serially, so one collection caps the
/// integration tests at one thread; the shards are separate collections with their own factory
/// and databases, so they run in parallel with each other (#1805).
/// Classes that share a static, factory-derived host (the four <c>MealPhotoTestHost</c> users) must stay
/// in one shard: the host is memoized from the first factory that asks, so a class in another shard would
/// call a host bound to a different database.
/// </summary>
[CollectionDefinition(TestCollection.Shard2)]
public class TestCollectionShard2 : ICollectionFixture<FitnessApiFactory>;

/// <summary>Third integration shard, see <see cref="TestCollectionShard2"/>.</summary>
[CollectionDefinition(TestCollection.Shard3)]
public class TestCollectionShard3 : ICollectionFixture<FitnessApiFactory>;

/// <summary>Fourth integration shard, see <see cref="TestCollectionShard2"/>.</summary>
[CollectionDefinition(TestCollection.Shard4)]
public class TestCollectionShard4 : ICollectionFixture<FitnessApiFactory>;
