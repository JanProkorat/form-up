namespace FormUp.Tests.Seed.WalkSeed;

/// <summary>
/// Collection that shares one <see cref="WalkSeedFixture"/> (one database, two seed runs) across the walk seed facts.
/// </summary>
[CollectionDefinition("WalkSeedTests")]
public class WalkSeedTestsCollection : ICollectionFixture<WalkSeedFixture>;
