using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.Auth;

/// <summary>
/// IStartupFilter that prepends a middleware reading X-Test-Client-IP and
/// writing the parsed address into Connection.RemoteIpAddress.  This gives
/// the rate-limiter a stable, controllable partition key under TestServer
/// (which otherwise sets RemoteIpAddress = null because there is no real TCP
/// socket, causing the fallback to per-request Connection.Id and making the
/// FixedWindow buckets never exhaustible from a test).
///
/// Registered only by <see cref="RateLimitEnabledFactory"/>; production code
/// never references this class.
/// </summary>
internal sealed class TestClientIpStartupFilter : IStartupFilter
{
    public const string Header = "X-Test-Client-IP";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(Header, out var ipValue)
                    && !string.IsNullOrWhiteSpace(ipValue))
                {
                    if (System.Net.IPAddress.TryParse(ipValue!, out var parsed))
                        context.Connection.RemoteIpAddress = parsed;
                }

                await nextMiddleware(context);
            });

            next(app);
        };
    }
}

/// <summary>
/// WebApplicationFactory override that:
/// (a) re-enables rate limiting (overrides FitnessApiFactory's RateLimiting:Disabled=true), and
/// (b) registers <see cref="TestClientIpStartupFilter"/> so tests can steer the rate-limit
///     partition key via an X-Test-Client-IP request header.
/// </summary>
public class RateLimitEnabledFactory(SharedTestContainers sharedContainers)
    : WebApplicationFactory<Program>, IAsyncLifetime
{
    private string _postgresConnectionString = string.Empty;
    private string _mongoDatabaseName = string.Empty;

    /// <summary>Lets <c>TestHelpers.RegisterAsync</c> confirm registered accounts in this host's database (#1517).</summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        TestHelpers.RememberHost(client, this);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Provide required secrets
        builder.UseSetting("POSTGRES_PASSWORD", "test");
        builder.UseSetting("MONGO_PASSWORD", "test");
        builder.UseSetting("MINIO_ACCESS_KEY", "test");
        builder.UseSetting("MINIO_SECRET_KEY", "test");
        builder.UseSetting("JWT_SECRET", new string('x', 64));

        // KEY DIFFERENCE from FitnessApiFactory: rate limiting is ENABLED
        builder.UseSetting("RateLimiting:Disabled", "false");

        builder.UseSetting("ConnectionStrings:PostgreSQl",
            "Host=localhost;Database=placeholder;Username=postgres");
        builder.UseSetting("ConnectionStrings:MongoDB",
            "mongodb://localhost:27017");

        builder.ConfigureServices(services =>
        {
            // Prepend the test IP middleware via IStartupFilter so we can control
            // Connection.RemoteIpAddress (partition key) from test code.
            services.AddSingleton<IStartupFilter, TestClientIpStartupFilter>();

            // Replace DbContext with testcontainer PostgreSQL
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
            if (descriptor is not null)
                services.Remove(descriptor);

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(_postgresConnectionString)
                    .ConfigureWarnings(w => w.Ignore(
                        Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

            // Replace MongoDB with testcontainer
            var mongoDbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IMongoDatabase));
            if (mongoDbDescriptor is not null)
                services.Remove(mongoDbDescriptor);

            var mongoContextDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IMongoContext));
            if (mongoContextDescriptor is not null)
                services.Remove(mongoContextDescriptor);

            services.AddSingleton<IMongoDatabase>(_ =>
            {
                var client = new MongoClient(sharedContainers.MongoConnectionString);
                return client.GetDatabase(_mongoDatabaseName);
            });
            services.AddSingleton<IMongoContext, MongoContext>();

            // Replace email service with fake
            var emailDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IEmailService));
            if (emailDescriptor is not null)
                services.Remove(emailDescriptor);
            services.AddSingleton<FakeEmailService>();
            services.AddSingleton<IEmailService>(sp => sp.GetRequiredService<FakeEmailService>());

            // Replace blob storage with fake
            var blobDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IBlobStorageService));
            if (blobDescriptor is not null)
                services.Remove(blobDescriptor);
            services.AddSingleton<IBlobStorageService, FakeBlobStorageService>();

            // Replace realtime notifier with fake
            var notifierDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IRealtimeNotifier));
            if (notifierDescriptor is not null)
                services.Remove(notifierDescriptor);
            services.AddSingleton<FakeRealtimeNotifier>();
            services.AddSingleton<IRealtimeNotifier>(sp => sp.GetRequiredService<FakeRealtimeNotifier>());

            // Replace push notifications with fake
            var pushDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IPushNotificationService));
            if (pushDescriptor is not null)
                services.Remove(pushDescriptor);
            services.AddSingleton<FakePushNotificationService>();
            services.AddSingleton<IPushNotificationService>(
                sp => sp.GetRequiredService<FakePushNotificationService>());

            // #726: prevent the background schedulers/worker from starting in this
            // test host — see TestHostedServiceExtensions for the root cause.
            services.RemoveBackgroundHostedServices();
        });

        builder.UseEnvironment("Development");
    }

    public async ValueTask InitializeAsync()
    {
        _postgresConnectionString = await sharedContainers.CreatePostgresDatabaseAsync("ratelimit");
        _mongoDatabaseName = SharedTestContainers.CreateMongoDatabaseName("ratelimit");

        FastEndpointsBootGate.Start(this);

        await ApplicationDbContextSeed.SeedAsync(Services);
    }

    // Intentionally skip base.DisposeAsync() — see FitnessApiFactory's matching remark. No
    // Testcontainer to dispose here anymore (#1104 Phase B); SharedTestContainers owns that.
    public new ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Defines a dedicated collection so <see cref="RateLimitEnabledFactory"/> boots ONCE for
/// the whole class instead of per test (#1104) — see <see cref="RateLimitPolicyTests"/>'
/// remarks for how the shared in-memory rate-limiter state is kept isolated per fact.
/// </summary>
[CollectionDefinition("RateLimitPolicyTests")]
public class RateLimitPolicyTestsCollection : ICollectionFixture<RateLimitEnabledFactory>;

/// <summary>
/// Integration tests for the split rate-limit policy introduced in issue #521.
/// Uses <see cref="RateLimitEnabledFactory"/> which keeps rate limiting active and
/// allows tests to steer the partition key via the X-Test-Client-IP header.
///
/// Each test uses a dedicated client that always sends a fixed X-Test-Client-IP so all
/// requests from that client land in the SAME rate-limit bucket — making exhaustion
/// assertions meaningful and deterministic.
/// </summary>
/// <remarks>
/// The ASP.NET Core rate limiter keeps its partition buckets in process memory for the
/// lifetime of the host. Now that <see cref="RateLimitEnabledFactory"/> is shared across
/// the whole class instead of booted fresh per test (#1104), a fixed IP literal reused by
/// two facts would let one fact's exhausted bucket leak into the next. Each fact below
/// generates its own fresh IP via <see cref="UniqueTrustedIp"/> / <see cref="UniqueUntrustedIp"/>
/// rather than sharing a class-level constant, so no two facts — now or in the future — can
/// collide on the same partition key.
/// </remarks>
[Collection("RateLimitPolicyTests")]
public class RateLimitPolicyTests(RateLimitEnabledFactory factory)
{
    private readonly RateLimitEnabledFactory _factory = factory;

    /// <summary>
    /// Creates an HttpClient that stamps every request with a fixed X-Test-Client-IP so
    /// all requests from this client share the same rate-limit partition.
    /// </summary>
    private HttpClient CreateClientWithIp(string ip)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add(TestClientIpStartupFilter.Header, ip);
        return client;
    }

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@ratelimit-test.com";

    private static int _trustedIpCounter;
    private static int _untrustedIpCounter;

    /// <summary>
    /// A fresh IP inside the 10.0.0.0/8 private range trusted by UseForwardedHeaders
    /// (KnownIPNetworks in Program.cs). Counter-based, so no two calls in a run collide
    /// in the collection-scoped host's rate-limit buckets (wraps after 16M calls).
    /// </summary>
    private static string UniqueTrustedIp()
    {
        var next = (uint)Interlocked.Increment(ref _trustedIpCounter) % 16_777_215u + 1;
        return $"10.{(next >> 16) & 255}.{(next >> 8) & 255}.{next & 255}";
    }

    /// <summary>
    /// A fresh IP in the TEST-NET-3 documentation range (203.0.113.0/24) — public, not in
    /// any KnownIPNetworks range. Counter-based like <see cref="UniqueTrustedIp"/>; the
    /// 254 host addresses only repeat after 254 calls (this file makes 2 per run).
    /// </summary>
    private static string UniqueUntrustedIp() =>
        $"203.0.113.{(uint)Interlocked.Increment(ref _untrustedIpCounter) % 254u + 1}";

    // ---------------------------------------------------------------------------
    // AC-4: /auth/refresh on its own policy cannot exhaust the /auth/login budget
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Proves that the FixedWindow limiter can actually be exhausted when all requests
    /// share the same IP partition.  If the IP is controllable, 10 login attempts from
    /// the same IP must yield a 429 on the 11th call.
    ///
    /// This is the baseline assertion that confirms the TestServer partition-key injection
    /// is working.  Without it, every request lands in its own unique bucket (fallback
    /// Connection.Id) and the limiter NEVER triggers — making all subsequent tests vacuous.
    /// </summary>
    [Fact]
    public async Task LoginBudget_IsExhaustedAfter10Attempts_SameIp()
    {
        using var client = CreateClientWithIp(UniqueTrustedIp());

        // Exhaust the login budget: PermitLimit = 10 for AppPolicies.AuthRateLimit.
        // We use 10 different emails so we don't hit a 401 (wrong-password) that might
        // confuse the assertion.  The /auth/login endpoint does not require the user to
        // exist for the rate limiter to decrement the permit — the limiter runs BEFORE
        // the handler validates credentials.
        for (var i = 0; i < 10; i++)
        {
            var response = await client.PostAsJsonAsync("/auth/login", new
            {
                Email = $"bogus-{i}@exhaustion-test.com",
                Password = "anything"
            });
            // Anything except 429 is acceptable here (401, 400, etc.)
            response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests,
                $"login attempt #{i + 1} should not be rate-limited yet (budget = 10)");
        }

        // The 11th attempt from the same IP must be blocked.
        var rateLimitedResponse = await client.PostAsJsonAsync("/auth/login", new
        {
            Email = "bogus-final@exhaustion-test.com",
            Password = "anything"
        });

        rateLimitedResponse.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "the 11th /auth/login from the same IP must be rate-limited (budget = 10 per 15 min)");
    }

    /// <summary>
    /// AC-4 regression: /auth/refresh lives on the 'auth-refresh' policy (PermitLimit=120),
    /// NOT the 'auth' policy (PermitLimit=10).
    ///
    /// We issue 10 /auth/refresh calls from IP2, then verify that /auth/login from IP2 is
    /// still allowed — proving the refresh calls did NOT consume the login budget.
    ///
    /// Without the policy split (i.e. if refresh were on AppPolicies.AuthRateLimit), the
    /// 10 refresh calls would exhaust the 10-permit login bucket and the login would 429.
    /// </summary>
    [Fact]
    public async Task RefreshCalls_DoNotExhaustLoginBudget()
    {
        using var client = CreateClientWithIp(UniqueTrustedIp());

        // Register + login to obtain a valid refresh token chain.
        // Registration calls /auth/register which is on the 'auth' policy —
        // that consumes 1 permit from Ip2's login bucket.
        var email = UniqueEmail();
        const string password = "TestPass1!";
        var registerResponse = await TestHelpers.RegisterAsync(client, email, password, "Rate", "Tester", "Client");
        registerResponse.IsSuccessStatusCode.Should().BeTrue("registration must succeed");

        var (_, refreshToken) = await TestHelpers.LoginAsync(client, email, password);
        // 2 permits consumed from Ip2's login budget (register + login).

        // Issue 8 more /auth/refresh calls — each rotates the token.
        // If refresh were sharing the 'auth' budget, 8 more + 2 earlier = 10 total,
        // leaving 0 permits for the final login check.
        for (var i = 0; i < 8; i++)
        {
            var refreshResponse = await client.PostAsJsonAsync("/auth/refresh", new { RefreshToken = refreshToken });
            refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK,
                $"refresh call #{i + 1} must succeed — auth-refresh policy has 120-permit budget");

            var body = await refreshResponse.Content.ReadFromJsonAsync<RefreshResult>();
            refreshToken = body!.RefreshToken;
        }
        // 10 total requests against 'auth' budget if policies were shared —
        // the next /auth/login would 429.

        // Now try /auth/login. With the split policy the login budget has 8 permits
        // remaining (only the register + initial login consumed from it), so this must succeed.
        var loginResponse = await client.PostAsJsonAsync("/auth/login", new
        {
            Email = email,
            Password = password
        });

        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "login must succeed after 8 refresh calls — /auth/refresh uses its OWN budget (auth-refresh), not the login budget (auth)");
    }

    /// <summary>
    /// Complementary AC-4 assertion: /auth/login does NOT exhaust the /auth/refresh budget.
    ///
    /// We exhaust Ip3's login budget (10 calls) then verify that /auth/refresh from Ip3
    /// is NOT blocked by the login exhaustion — the refresh endpoint is on a separate policy.
    /// </summary>
    [Fact]
    public async Task ExhaustedLoginBudget_DoesNotBlock_RefreshEndpoint()
    {
        using var client = CreateClientWithIp(UniqueTrustedIp());

        // Seed a user and obtain a refresh token BEFORE exhausting the budget.
        var email = UniqueEmail();
        const string password = "TestPass1!";
        var registerResponse = await TestHelpers.RegisterAsync(client, email, password, "Rate", "Seed", "Client");
        registerResponse.IsSuccessStatusCode.Should().BeTrue("registration must succeed");

        var (_, refreshToken) = await TestHelpers.LoginAsync(client, email, password);
        // 2 of 10 permits consumed.

        // Exhaust the remaining 8 permits on the login budget.
        for (var i = 0; i < 8; i++)
        {
            await client.PostAsJsonAsync("/auth/login", new
            {
                Email = $"bogus-{i}@exhaust-budget.com",
                Password = "anything"
            });
        }
        // Now Ip3's login bucket is fully exhausted.

        // Confirm the login bucket is indeed exhausted.
        var blockedLogin = await client.PostAsJsonAsync("/auth/login", new
        {
            Email = email,
            Password = password
        });
        blockedLogin.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "Ip3's login bucket must be exhausted (10 permits used)");

        // /auth/refresh must NOT be blocked — it is on the separate 'auth-refresh' policy.
        var refreshResponse = await client.PostAsJsonAsync("/auth/refresh", new { RefreshToken = refreshToken });
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "/auth/refresh must succeed even when the login budget is exhausted — it is on the 'auth-refresh' policy, not the 'auth' policy");
    }

    // ---------------------------------------------------------------------------
    // AC-5: per-IP partitioning via trusted X-Forwarded-For
    // ---------------------------------------------------------------------------

    /// <summary>
    /// AC-5: Two distinct X-Forwarded-For values from the SAME underlying TestServer
    /// connection are treated as SEPARATE rate-limit partitions.
    ///
    /// We exhaust the login budget from forwardedIp1 (10 calls → 429), then confirm
    /// that a login from forwardedIp2 — a DIFFERENT forwarded IP — is still allowed.
    /// This proves that the partition key is the resolved IP, not a shared fallback.
    ///
    /// For UseForwardedHeaders to honor the X-Forwarded-For header, the immediate peer
    /// (Connection.RemoteIpAddress as set by TestClientIpStartupFilter) must be in a
    /// KnownIPNetworks range in Program.cs.  We use 10.0.2.1 (inside 10.0.0.0/8) as
    /// the trusted proxy IP so the header is actually applied.
    /// </summary>
    [Fact]
    public async Task ForwardedFor_DifferentIPs_ArePartitionedSeparately()
    {
        // Use a trusted proxy IP as the immediate peer — 10.0.0.0/8 is in KnownIPNetworks.
        var trustedProxyIp = UniqueTrustedIp();
        var forwardedIp1   = UniqueUntrustedIp(); // public "client IP" #1
        var forwardedIp2   = UniqueUntrustedIp(); // public "client IP" #2

        // Client whose underlying TestServer connection looks like it's coming from
        // the trusted proxy. Every request also carries an X-Forwarded-For header that
        // UseForwardedHeaders will rewrite Connection.RemoteIpAddress with.
        using var client = CreateClientWithIp(trustedProxyIp);

        // Helper: send a login from a specific forwarded IP.
        async Task<HttpResponseMessage> LoginFromIp(string forwarded, string email, string pw)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/auth/login");
            req.Headers.Add("X-Forwarded-For", forwarded);
            req.Content = JsonContent.Create(new { Email = email, Password = pw });
            return await client.SendAsync(req);
        }

        // Exhaust forwardedIp1's login budget: 10 attempts.
        for (var i = 0; i < 10; i++)
        {
            var response = await LoginFromIp(forwardedIp1, $"bogus-fwd-{i}@partition-test.com", "anything");
            response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests,
                $"attempt #{i + 1} from {forwardedIp1} must not be rate-limited yet");
        }

        // Confirm forwardedIp1 is now exhausted.
        var blockedResponse = await LoginFromIp(forwardedIp1, "extra@partition-test.com", "anything");
        blockedResponse.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            $"the 11th attempt from {forwardedIp1} must be rate-limited (budget = 10)");

        // forwardedIp2 has its own independent bucket — must still be allowed.
        var allowedResponse = await LoginFromIp(forwardedIp2, "other@partition-test.com", "anything");
        allowedResponse.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests,
            $"login from {forwardedIp2} must succeed — it has its own partition independent of {forwardedIp1}");
    }

    // ---------------------------------------------------------------------------
    // claude-security F8: /trainer/pending-invites is partitioned by the
    // authenticated professional's UserId claim, not by IP.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Proves AppPolicies.PendingInviteRateLimit's partition key is the caller's UserId claim,
    /// not the request IP. Two different professionals sharing the SAME IP must NOT share a
    /// bucket: exhausting professional #1's 30-permit budget must not affect professional #2.
    /// If the partition key ever regressed to <c>GetPartitionKey(context)</c> (the IP-based
    /// fallback used by the login/refresh policies), professional #2's very first call from the
    /// same IP would already be blocked — this test would fail immediately rather than after
    /// 30 calls.
    /// </summary>
    [Fact]
    public async Task PendingInviteBudget_PartitionedByProfessional_NotSharedAcrossAccountsOnSameIp()
    {
        var sharedIp = UniqueTrustedIp();
        const int permitLimit = 30;

        using var client1 = CreateClientWithIp(sharedIp);
        using var client2 = CreateClientWithIp(sharedIp);

        var professional1Email = UniqueEmail();
        await TestHelpers.RegisterAsync(client1, professional1Email, "TestPass1!", "Coach", "One", "Trainer");
        var (professional1Access, _) = await TestHelpers.LoginAsync(client1, professional1Email, "TestPass1!");
        TestHelpers.SetBearerToken(client1, professional1Access);

        var professional2Email = UniqueEmail();
        await TestHelpers.RegisterAsync(client2, professional2Email, "TestPass1!", "Coach", "Two", "Trainer");
        var (professional2Access, _) = await TestHelpers.LoginAsync(client2, professional2Email, "TestPass1!");
        TestHelpers.SetBearerToken(client2, professional2Access);

        // Exhaust professional #1's pending-invite budget (PermitLimit = 30).
        for (var i = 0; i < permitLimit; i++)
        {
            var response = await client1.PostAsJsonAsync("/trainer/pending-invites", new
            {
                FirstName = "Invitee",
                LastName = $"{i}",
                Email = $"invitee-{i}-{Guid.NewGuid():N}@ratelimit-test.com"
            });
            response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests,
                $"pending-invite call #{i + 1} from professional #1 should not be rate-limited yet (budget = {permitLimit})");
        }

        // Confirm professional #1's bucket is now exhausted — the baseline that makes the
        // cross-account assertion below meaningful.
        var exhausted = await client1.PostAsJsonAsync("/trainer/pending-invites", new
        {
            FirstName = "Invitee",
            LastName = "Final",
            Email = $"invitee-final-{Guid.NewGuid():N}@ratelimit-test.com"
        });
        exhausted.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            $"professional #1's pending-invite budget must be exhausted after {permitLimit} permits");

        // Professional #2, sharing the SAME IP, must NOT be blocked — the partition key is the
        // authenticated professional's UserId claim, not the request IP.
        var stillAllowed = await client2.PostAsJsonAsync("/trainer/pending-invites", new
        {
            FirstName = "Invitee",
            LastName = "FromOther",
            Email = $"invitee-other-{Guid.NewGuid():N}@ratelimit-test.com"
        });
        stillAllowed.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests,
            "professional #2 must have their own independent bucket even when sharing the same IP as professional #1");
    }

    /// <summary>
    /// The live slot check has its own per-professional budget of 60: exhausting it answers 429,
    /// another professional on the same IP is unaffected, and the same professional's create-invite
    /// budget is untouched.
    /// </summary>
    [Fact]
    public async Task PendingInviteSlotCheckBudget_PerProfessional_LeavesCreateBudgetAndOthersUntouched()
    {
        var sharedIp = UniqueTrustedIp();
        const int permitLimit = 60;

        using var client1 = CreateClientWithIp(sharedIp);
        using var client2 = CreateClientWithIp(sharedIp);

        var professional1Email = UniqueEmail();
        await TestHelpers.RegisterAsync(client1, professional1Email, "TestPass1!", "Coach", "One", "Trainer");
        var (professional1Access, _) = await TestHelpers.LoginAsync(client1, professional1Email, "TestPass1!");
        TestHelpers.SetBearerToken(client1, professional1Access);

        var professional2Email = UniqueEmail();
        await TestHelpers.RegisterAsync(client2, professional2Email, "TestPass1!", "Coach", "Two", "Trainer");
        var (professional2Access, _) = await TestHelpers.LoginAsync(client2, professional2Email, "TestPass1!");
        TestHelpers.SetBearerToken(client2, professional2Access);

        var slotCheckUrl = $"/trainer/pending-invites/slot-check?email=slot-{Guid.NewGuid():N}@ratelimit-test.com";

        for (var i = 0; i < permitLimit; i++)
        {
            var response = await client1.GetAsync(slotCheckUrl);
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                $"slot check #{i + 1} should still be inside the budget of {permitLimit}");
        }

        var exhausted = await client1.GetAsync(slotCheckUrl);
        exhausted.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var otherProfessional = await client2.GetAsync(slotCheckUrl);
        otherProfessional.StatusCode.Should().Be(HttpStatusCode.OK,
            "another professional on the same IP has an independent slot-check bucket");

        var createInvite = await client1.PostAsJsonAsync("/trainer/pending-invites", new
        {
            Email = $"invitee-{Guid.NewGuid():N}@ratelimit-test.com"
        });
        createInvite.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests,
            "slot checks must not consume the create-invite budget");
    }

    private record RefreshResult(string AccessToken, string RefreshToken, DateTime ExpiresAt);
}
