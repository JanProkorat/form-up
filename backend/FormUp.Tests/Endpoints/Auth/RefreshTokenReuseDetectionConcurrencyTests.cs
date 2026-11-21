using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Features.Auth.RefreshToken;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.Auth;

// ── Test factory ─────────────────────────────────────────────────────────────

/// <summary>
/// Dedicated factory for the refresh-token reuse/theft-detection concurrency
/// tests. Runs against real Postgres (via Testcontainers) because the atomic
/// conditional update (<c>WHERE Token = @t AND RevokedAt IS NULL</c>) that
/// closes the rotation race cannot be exercised meaningfully against the
/// NSubstitute-backed <c>MockDbBuilder</c> unit tests — this needs a real
/// database enforcing the race.
///
/// Runs in its own collection + factory (separate Testcontainers) to avoid
/// polluting the shared Integration collection's database with concurrency
/// scenarios that fire raw concurrent HTTP requests.
/// </summary>
public class RefreshTokenConcurrencyFactory(SharedTestContainers sharedContainers)
    : WebApplicationFactory<Program>, IAsyncLifetime
{
    private string _postgresConnectionString = string.Empty;
    private string _mongoDatabaseName = string.Empty;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("POSTGRES_PASSWORD", "test");
        builder.UseSetting("MONGO_PASSWORD", "test");
        builder.UseSetting("MINIO_ACCESS_KEY", "test");
        builder.UseSetting("MINIO_SECRET_KEY", "test");
        builder.UseSetting("JWT_SECRET", new string('x', 64));
        builder.UseSetting("RateLimiting:Disabled", "true");

        builder.UseSetting("ConnectionStrings:PostgreSQl",
            "Host=localhost;Database=placeholder;Username=postgres");
        builder.UseSetting("ConnectionStrings:MongoDB",
            "mongodb://localhost:27017");

        builder.ConfigureServices(services =>
        {
            // Replace DbContext with Testcontainer-backed Postgres.
            var pgDesc = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
            if (pgDesc is not null) services.Remove(pgDesc);

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(_postgresConnectionString)
                    .ConfigureWarnings(w => w.Ignore(
                        Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

            // Replace MongoDB with Testcontainer (required for host bootstrap;
            // unused by these tests).
            var mongoDbDesc = services.SingleOrDefault(d => d.ServiceType == typeof(IMongoDatabase));
            if (mongoDbDesc is not null) services.Remove(mongoDbDesc);

            var mongoCtxDesc = services.SingleOrDefault(d => d.ServiceType == typeof(IMongoContext));
            if (mongoCtxDesc is not null) services.Remove(mongoCtxDesc);

            services.AddSingleton<IMongoDatabase>(_ =>
            {
                var client = new MongoClient(sharedContainers.MongoConnectionString);
                return client.GetDatabase(_mongoDatabaseName);
            });
            services.AddSingleton<IMongoContext, MongoContext>();

            // Replace external services with fakes.
            var emailDesc = services.SingleOrDefault(
                d => d.ServiceType == typeof(Application.Domain.Interfaces.IEmailService));
            if (emailDesc is not null) services.Remove(emailDesc);
            services.AddSingleton<FakeEmailService>();
            services.AddSingleton<Application.Domain.Interfaces.IEmailService>(
                sp => sp.GetRequiredService<FakeEmailService>());

            var notifierDesc = services.SingleOrDefault(
                d => d.ServiceType == typeof(Application.Domain.Interfaces.IRealtimeNotifier));
            if (notifierDesc is not null) services.Remove(notifierDesc);
            services.AddSingleton<FakeRealtimeNotifier>();
            services.AddSingleton<Application.Domain.Interfaces.IRealtimeNotifier>(
                sp => sp.GetRequiredService<FakeRealtimeNotifier>());

            var blobDesc = services.SingleOrDefault(
                d => d.ServiceType == typeof(Application.Domain.Interfaces.IBlobStorageService));
            if (blobDesc is not null) services.Remove(blobDesc);
            services.AddSingleton<Application.Domain.Interfaces.IBlobStorageService, FakeBlobStorageService>();

            var pushDesc = services.SingleOrDefault(
                d => d.ServiceType == typeof(Application.Domain.Interfaces.IPushNotificationService));
            if (pushDesc is not null) services.Remove(pushDesc);
            services.AddSingleton<FakePushNotificationService>();
            services.AddSingleton<Application.Domain.Interfaces.IPushNotificationService>(
                sp => sp.GetRequiredService<FakePushNotificationService>());

            // #726: prevent the background schedulers/worker from starting in this
            // test host — see TestHostedServiceExtensions for the root cause.
            services.RemoveBackgroundHostedServices();
        });

        builder.UseEnvironment("Development");
    }

    public async ValueTask InitializeAsync()
    {
        _postgresConnectionString = await sharedContainers.CreatePostgresDatabaseAsync("refresh_concurrency");
        _mongoDatabaseName = SharedTestContainers.CreateMongoDatabaseName("refresh_concurrency");

        FastEndpointsBootGate.Start(this);

        await ApplicationDbContextSeed.SeedAsync(Services);
    }

    // Intentionally skip base.DisposeAsync() — see FitnessApiFactory's matching remark. No
    // Testcontainer to dispose here anymore (#1104 Phase B); SharedTestContainers owns that.
    public new ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// ── Collection definition ─────────────────────────────────────────────────────

/// <summary>
/// Boots <see cref="RefreshTokenConcurrencyFactory"/> ONCE for the whole
/// collection (mirrors <c>TestCollection : ICollectionFixture&lt;FitnessApiFactory&gt;</c>
/// in <see cref="Infrastructure.TestCollection"/>) rather than per-test.
/// </summary>
/// <remarks>
/// A per-test <c>IAsyncLifetime</c> on the test class (the original shape of
/// this fixture) creates and disposes a full <see cref="WebApplicationFactory{TEntryPoint}"/>
/// host TWICE mid-suite-run — once per <c>[Fact]</c> — interleaved with
/// dozens of unrelated standalone <c>Factory.Create&lt;T&gt;()</c> unit tests
/// running concurrently across the suite (xUnit parallelizes collections by
/// default). Disposing a host also tears down its <c>IServiceProvider</c>,
/// and FastEndpoints' <c>ServiceResolver.Provider</c> is a process-global
/// static — whichever host booted most recently owns it. If this fixture's
/// host happens to be the one currently registered when its per-test
/// disposal runs, every standalone test resolving a service at that instant
/// throws <see cref="ObjectDisposedException"/>, even though the failing
/// test has nothing to do with refresh tokens. Confirmed via a full-suite
/// baseline run without this factory (0 such failures) vs. with the
/// per-test-lifetime shape (51 scattered failures across unrelated
/// features). Using a collection fixture makes this host boot/dispose
/// exactly once, at the very start/end of its collection — the same
/// low-frequency lifecycle every other Testcontainers-backed factory in
/// this suite already relies on for safety.
/// </remarks>
[CollectionDefinition("RefreshTokenConcurrency")]
public class RefreshTokenConcurrencyCollection : ICollectionFixture<RefreshTokenConcurrencyFactory>;

// ── Tests ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Integration tests proving #652's grace-window reuse-detection discriminator
/// against real Postgres: the atomic conditional update
/// (<c>WHERE Token = @t AND RevokedAt IS NULL</c>) closes the rotation race so
/// exactly one concurrent caller wins, and replay of an already-rotated token
/// outside the grace window is treated as theft (whole family revoked).
/// </summary>
[Collection("RefreshTokenConcurrency")]
public class RefreshTokenReuseDetectionConcurrencyTests(RefreshTokenConcurrencyFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds a fresh client user + an active refresh token directly against
    /// Postgres, returning the token string and the owning user id.
    /// </summary>
    private async Task<(string Token, Guid UserId)> SeedActiveUserAndTokenAsync(CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            UserName = $"reuse-test-{Guid.NewGuid():N}@test.com",
            Email = $"reuse-test-{Guid.NewGuid():N}@test.com",
            EmailConfirmed = true,
            IsActive = true
        };
        var createResult = await userManager.CreateAsync(user, "Test-Password-123!");
        createResult.Succeeded.Should().BeTrue(
            string.Join(", ", createResult.Errors.Select(e => e.Description)));
        await userManager.AddToRoleAsync(user, AppRoles.Client);

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var tokenValue = $"seed-token-{Guid.NewGuid():N}";
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            Token = tokenValue,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        await db.SaveChangesAsync(ct);

        return (tokenValue, user.Id);
    }

    private async Task<List<RefreshToken>> GetFamilyAsync(Guid userId, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.RefreshTokens
            .AsNoTracking()
            .Where(rt => rt.UserId == userId)
            .ToListAsync(ct);
    }

    private async Task ForceRevokedAtAsync(string token, DateTime revokedAt, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.RefreshTokens
            .Where(rt => rt.Token == token)
            .ExecuteUpdateAsync(s => s.SetProperty(rt => rt.RevokedAt, revokedAt), ct);
    }

    // ── (1) Concurrent same-token double-fire → benign reconcile ─────────────

    /// <summary>
    /// Two concurrent /auth/refresh calls with the SAME token (simulating a
    /// client-side retry racing its own successful request) must both
    /// succeed: exactly one wins the atomic rotation and mints a new token,
    /// the loser reconciles benignly to the SAME successor. The family must
    /// NOT be revoked, and exactly one new token row must be created.
    /// </summary>
    [Fact]
    public async Task ConcurrentSameTokenDoubleFire_BothCallersAuthenticated_FamilyNotRevoked()
    {
        var ct = TestContext.Current.CancellationToken;
        var (token, userId) = await SeedActiveUserAndTokenAsync(ct);

        var request1 = _client.PostAsJsonAsync("/auth/refresh", new { RefreshToken = token }, ct);
        var request2 = _client.PostAsJsonAsync("/auth/refresh", new { RefreshToken = token }, ct);

        var responses = await Task.WhenAll(request1, request2);

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK,
            "both concurrent callers on the same token are legitimate — no theft occurred");

        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<RefreshTokenResponse>(ct)));
        var refreshTokens = bodies.Select(b => b!.RefreshToken).Distinct().ToList();

        refreshTokens.Should().HaveCount(1,
            "the winner mints one new token and the loser must reconcile to that SAME successor");
        bodies.Should().OnlyContain(b => !string.IsNullOrEmpty(b!.AccessToken),
            "both callers must receive a usable access token");

        var family = await GetFamilyAsync(userId, ct);
        family.Should().HaveCount(2, "one original (now rotated) token + exactly one new successor — no duplicate inserts");

        var original = family.Single(rt => rt.Token == token);
        original.IsRevoked.Should().BeTrue("the original token was rotated");
        original.ReplacedByToken.Should().Be(refreshTokens[0]);

        var successor = family.Single(rt => rt.Token == refreshTokens[0]);
        successor.IsRevoked.Should().BeFalse("the successor token must remain active — the family was not revoked");
    }

    // ── (2) Replay outside grace window → theft, family revoked ──────────────

    /// <summary>
    /// A token that was legitimately rotated, then replayed well outside the
    /// reuse grace window (simulating a stolen/leaked token being redeemed
    /// long after the legitimate client already rotated it), must be treated
    /// as theft: the entire token family for that user is revoked and the
    /// replay is rejected. A subsequent attempt with the (now-revoked)
    /// successor must also fail.
    /// </summary>
    [Fact]
    public async Task ReplayOutsideGraceWindow_RevokesWholeFamily_AndRejectsBothTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var (originalToken, userId) = await SeedActiveUserAndTokenAsync(ct);

        // Legitimate rotation.
        var firstResponse = await _client.PostAsJsonAsync("/auth/refresh", new { RefreshToken = originalToken }, ct);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<RefreshTokenResponse>(ct);
        var successorToken = firstBody!.RefreshToken;

        // Simulate time passing well beyond the grace window since rotation.
        await ForceRevokedAtAsync(originalToken, DateTime.UtcNow.AddMinutes(-5), ct);

        // Replay the original (now stale-revoked) token — this is reuse/theft.
        var replayResponse = await _client.PostAsJsonAsync("/auth/refresh", new { RefreshToken = originalToken }, ct);
        replayResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an already-rotated token replayed outside the grace window is theft and must be rejected");

        var family = await GetFamilyAsync(userId, ct);
        family.Should().OnlyContain(rt => rt.IsRevoked,
            "theft detection must revoke every active token in the family, including the legitimate successor");

        // The legitimate successor — which was still valid a moment ago — must
        // now also be rejected, because the whole family was burned.
        var successorAttempt = await _client.PostAsJsonAsync("/auth/refresh", new { RefreshToken = successorToken }, ct);
        successorAttempt.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the family-wide revocation must invalidate the legitimate successor too");
    }

    // ── (3) Rotate-then-insert atomicity (#694) ──────────────────────────────

    /// <summary>
    /// Proves <see cref="IApplicationDbContext.RotateRefreshTokenAsync"/>'s
    /// conditional UPDATE and successor INSERT commit together as a single
    /// transaction (#694): immediately after the call returns, a completely
    /// FRESH scope (a different <see cref="ApplicationDbContext"/> instance —
    /// not the one that performed the write) must see BOTH the predecessor's
    /// <c>ReplacedByToken</c> set AND the successor row present. There is no
    /// window where one is durable and the other is not.
    /// </summary>
    [Fact]
    public async Task RotateRefreshTokenAsync_SuccessorRow_AlwaysDurableAlongsideReplacedByToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var (token, userId) = await SeedActiveUserAndTokenAsync(ct);

        var successorTokenValue = $"successor-{Guid.NewGuid():N}";
        var rotatedAt = DateTime.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var successor = new RefreshToken
            {
                UserId = userId,
                Token = successorTokenValue,
                ExpiresAt = DateTime.UtcNow.AddDays(7)
            };

            var rowsAffected = await db.RotateRefreshTokenAsync(token, successor, rotatedAt, ct);
            rowsAffected.Should().Be(1, "this is the only caller — it must win the conditional update");
        }

        // Read back from a brand-new scope/DbContext (a different connection)
        // to prove the transaction actually committed both writes together,
        // not just made them visible within the writing context's own tracker.
        var family = await GetFamilyAsync(userId, ct);
        family.Should().HaveCount(2, "the predecessor plus its successor, both durably committed");

        var predecessor = family.Single(rt => rt.Token == token);
        predecessor.ReplacedByToken.Should().Be(successorTokenValue);
        predecessor.IsRevoked.Should().BeTrue();

        var successorRow = family.SingleOrDefault(rt => rt.Token == successorTokenValue);
        successorRow.Should().NotBeNull(
            "the successor row must always be present whenever ReplacedByToken references it — never a dangling reference left by two separate non-atomic writes");
    }
}
