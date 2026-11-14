using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Infrastructure;

/// <summary>
/// Custom WebApplicationFactory that uses the shared Postgres/Mongo Testcontainers (#1104
/// Phase B — see <see cref="SharedTestContainers"/>), each in its own database inside those
/// shared servers. Replaces the email service with a no-op fake and disables rate limiting
/// for tests.
/// </summary>
public class FitnessApiFactory(SharedTestContainers sharedContainers) : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Webhook signing secret the test host verifies against; a test-only constant, not a credential.</summary>
    public const string TestWebhookSecret = "test-only-webhook-signing-secret";

    private string _postgresConnectionString = string.Empty;
    private string _mongoDatabaseName = string.Empty;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Provide required secrets (test values only, not real credentials)
        builder.UseSetting("POSTGRES_PASSWORD", "test");
        builder.UseSetting("MONGO_PASSWORD", "test");
        builder.UseSetting("MINIO_ACCESS_KEY", "test");
        builder.UseSetting("MINIO_SECRET_KEY", "test");
        builder.UseSetting("JWT_SECRET", new string('x', 64));
        builder.UseSetting("RateLimiting:Disabled", "true");
        builder.UseSetting("Stripe:WebhookSecret", TestWebhookSecret);

        // Provide placeholder connection strings so Program.cs ConnectionStringFactory
        // does not throw during host startup.  The real DB contexts are replaced below
        // with Testcontainer-backed instances, so these values are never actually used.
        builder.UseSetting("ConnectionStrings:PostgreSQl",
            "Host=localhost;Database=placeholder;Username=postgres");
        builder.UseSetting("ConnectionStrings:MongoDB",
            "mongodb://localhost:27017");

        builder.ConfigureServices(services =>
        {
            // Replace DbContext to use testcontainer PostgreSQL
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
            if (descriptor is not null)
                services.Remove(descriptor);

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(_postgresConnectionString)
                    .ConfigureWarnings(w => w.Ignore(
                        Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

            // Replace MongoDB to use testcontainer
            var mongoDbDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IMongoDatabase));
            if (mongoDbDescriptor is not null)
                services.Remove(mongoDbDescriptor);

            var mongoContextDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IMongoContext));
            if (mongoContextDescriptor is not null)
                services.Remove(mongoContextDescriptor);

            services.AddSingleton<IMongoDatabase>(_ =>
            {
                var client = new MongoClient(sharedContainers.MongoConnectionString);
                return client.GetDatabase(_mongoDatabaseName);
            });
            services.AddSingleton<IMongoContext, MongoContext>();

            // Replace email service with fake
            var emailDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IEmailService));
            if (emailDescriptor is not null)
                services.Remove(emailDescriptor);

            services.AddSingleton<FakeEmailService>();
            services.AddSingleton<IEmailService>(sp => sp.GetRequiredService<FakeEmailService>());

            // Replace realtime notifier with in-memory fake so tests can assert broadcasts
            var notifierDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IRealtimeNotifier));
            if (notifierDescriptor is not null)
                services.Remove(notifierDescriptor);

            services.AddSingleton<FakeRealtimeNotifier>();
            services.AddSingleton<IRealtimeNotifier>(sp => sp.GetRequiredService<FakeRealtimeNotifier>());

            // Replace blob storage with a no-op fake so integration tests never
            // require a running MinIO instance.  IImageUploadService (the layer
            // above) is left as-is so its content-type / size validation still runs.
            var blobDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IBlobStorageService));
            if (blobDescriptor is not null)
                services.Remove(blobDescriptor);

            services.AddSingleton<IBlobStorageService, FakeBlobStorageService>();

            // Replace push notification service with in-memory fake so integration tests
            // can assert on sent pushes without a real Expo endpoint.
            var pushDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IPushNotificationService));
            if (pushDescriptor is not null)
                services.Remove(pushDescriptor);

            services.AddSingleton<FakePushNotificationService>();
            services.AddSingleton<IPushNotificationService>(
                sp => sp.GetRequiredService<FakePushNotificationService>());

            // Replace the Stripe seam with an in-memory fake so no test makes a network call and billing
            // behaviour can be asserted. Tests reset it before use; it is shared across the host.
            var subscriptionDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(ISubscriptionService));
            if (subscriptionDescriptor is not null)
                services.Remove(subscriptionDescriptor);

            services.AddSingleton<FakeSubscriptionService>();
            services.AddSingleton<ISubscriptionService>(
                sp => sp.GetRequiredService<FakeSubscriptionService>());

            // #1104: PBKDF2 at the production default (100,000 iterations) is deliberately slow
            // and dominates every real register/login round trip in the suite. Test-host-only —
            // never touches the production default in Program.cs.
            services.Configure<PasswordHasherOptions>(o => o.IterationCount = 1);

            // #726: prevent the background schedulers/worker from ever starting in
            // this test host — see TestHostedServiceExtensions for the full root
            // cause (zombie BackgroundService timers ticking against a disposed
            // Testcontainer, cascading via BackgroundServiceExceptionBehavior=StopHost).
            // Supersedes the #282/#299 finding that a narrower, single-type removal
            // predicate was a no-op: that attempt only targeted PhotoDiaryReminderScheduler
            // and left SocialLoginNonceReaperService/EmailDispatchWorker
            // running; this removes all four background loops while leaving each
            // singleton independently resolvable for direct-tick tests (verified by
            // FitnessApiFactoryTests).
            services.RemoveBackgroundHostedServices();
        });

        builder.UseEnvironment("Development");
    }

    public async ValueTask InitializeAsync()
    {
        _postgresConnectionString = await sharedContainers.CreatePostgresDatabaseAsync("fitness_api");
        _mongoDatabaseName = SharedTestContainers.CreateMongoDatabaseName("fitness_api");

        // Apply migrations and seed
        await ApplicationDbContextSeed.SeedAsync(Services);
    }

    public new ValueTask DisposeAsync()
    {
        // Intentionally skip base.DisposeAsync() — no Testcontainer to dispose here anymore
        // (#1104 Phase B: the shared containers outlive this factory and are disposed once by
        // SharedTestContainers itself), but the reason for skipping the base call is unchanged:
        // it disposes the root IServiceProvider, which clears FastEndpoints' process-global
        // ServiceResolver.Provider. Any Factory.Create<T>() call running concurrently
        // (standalone unit tests without a [Collection] attribute) would then throw
        // ObjectDisposedException. Skipping base.DisposeAsync() keeps the provider alive until
        // the process exits — safe for test code where the process is short-lived.
        //
        // See also: ResetEndpointFactoryBase (same pattern, #296).
        return ValueTask.CompletedTask;
    }
}
