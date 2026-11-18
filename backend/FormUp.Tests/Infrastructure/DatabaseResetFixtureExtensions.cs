using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Infrastructure;

/// <summary>
/// Test-only helper that resets a shared Postgres + Mongo pair back to a pristine, seeded state:
/// every table emptied (the migrated schema is kept, so no migration is re-applied) and every Mongo
/// collection dropped. Unlike the app's own <c>ResetTestStateEndpoint</c>
/// (<c>Features/Testing/Reset/ResetTestStateEndpoint.cs</c>), it does not drop the schema, which
/// costs a full migration run per reset.
///
/// Part of #1104: several test classes used to boot a brand-new
/// <c>WebApplicationFactory</c> + Testcontainers pair per <c>[Fact]</c> so every test
/// started from a genuinely empty database. Converting those classes to a
/// collection-shared factory (one boot per class instead of per test) is a large
/// wall-clock win, but it means facts that assert exact counts or exercise
/// seed-idempotency now share state across the class — call this from the test
/// class's own <see cref="IAsyncLifetime.InitializeAsync"/> to restore that pristine
/// starting point before each fact.
/// </summary>
public static class DatabaseResetFixtureExtensions
{
    // One statement empties every table but the migration history; CASCADE covers the foreign keys.
    private const string TruncateAllTablesSql = """
        DO $$
        DECLARE table_names text;
        BEGIN
            SELECT string_agg(format('%I.%I', schemaname, tablename), ', ') INTO table_names
            FROM pg_tables
            WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory';

            IF table_names IS NOT NULL THEN
                EXECUTE 'TRUNCATE TABLE ' || table_names || ' RESTART IDENTITY CASCADE';
            END IF;
        END $$;
        """;

    /// <summary>
    /// Empties every Postgres table and restarts its identity sequences (the schema and the migration history stay,
    /// so no migration is re-applied), drops every Mongo collection, then re-seeds Identity roles + the system admin
    /// user via <see cref="ApplicationDbContextSeed.SeedAsync"/>.
    /// </summary>
    /// <param name="services">The factory's root service provider.</param>
    public static async Task ResetPostgresAndMongoAsync(this IServiceProvider services)
    {
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlRawAsync(TruncateAllTablesSql);

            var mongoDatabase = scope.ServiceProvider.GetRequiredService<IMongoDatabase>();
            var collectionNames = await (await mongoDatabase.ListCollectionNamesAsync()).ToListAsync();
            foreach (var name in collectionNames)
            {
                await mongoDatabase.DropCollectionAsync(name);
            }

            // Recreate the indexes dropped with their collections, as ResetTestStateEndpoint does.
            await scope.ServiceProvider.GetRequiredService<MongoIndexInitializer>().StartAsync(CancellationToken.None);
        }

        // Re-applies migrations and re-seeds roles + the system admin user — mirrors
        // ResetTestStateEndpoint's own reset sequence in production.
        await ApplicationDbContextSeed.SeedAsync(services);
    }
}
