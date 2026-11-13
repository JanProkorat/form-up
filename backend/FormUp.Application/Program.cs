using System.Threading.RateLimiting;
using FastEndpoints;
using Microsoft.AspNetCore.HttpOverrides;
using FastEndpoints.Security;
using FastEndpoints.Swagger;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Cli;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.HealthChecks;
using FormUp.Application.Infrastructure.Hubs;
using FormUp.Application.Infrastructure.Services;
using FormUp.Application.Middleware;
using FormUp.Application.Seed;
using MongoDB.Driver;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Resend;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog
builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .WriteTo.Console());

// Secrets from environment variables (launchSettings.json in dev, App Settings in prod)
var postgresPassword = builder.Configuration["POSTGRES_PASSWORD"]
    ?? throw new InvalidOperationException("POSTGRES_PASSWORD environment variable is not set.");
var mongoPassword = builder.Configuration["MONGO_PASSWORD"]
    ?? throw new InvalidOperationException("MONGO_PASSWORD environment variable is not set.");
var minioAccessKey = builder.Configuration["MINIO_ACCESS_KEY"]
    ?? throw new InvalidOperationException("MINIO_ACCESS_KEY environment variable is not set.");
var minioSecretKey = builder.Configuration["MINIO_SECRET_KEY"]
    ?? throw new InvalidOperationException("MINIO_SECRET_KEY environment variable is not set.");
var jwtSecret = builder.Configuration["JWT_SECRET"]
    ?? throw new InvalidOperationException("JWT_SECRET environment variable is not set.");

// Inject secrets into configuration so services can read them
builder.Configuration["Jwt:Secret"] = jwtSecret;
builder.Configuration["MinIO:AccessKey"] = minioAccessKey;
builder.Configuration["MinIO:SecretKey"] = minioSecretKey;

// Build connection strings with injected passwords
var postgresConnection = ConnectionStringFactory.BuildPostgres(builder.Configuration);
var mongoConnection = ConnectionStringFactory.BuildMongo(builder.Configuration);

// PostgreSQL + EF Core
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(postgresConnection)
        .UseSnakeCaseNamingConvention());
builder.Services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());

// MongoDB — Guid serializer registered via MongoBootstrapper module
// initializer so it runs before any other assembly touches BsonSerializer.
var mongoDatabaseName = builder.Configuration[ConfigKeys.MongoDbDatabaseName]
    ?? throw new InvalidOperationException("MongoDB:DatabaseName is not configured.");
var mongoClient = new MongoClient(mongoConnection);
builder.Services.AddSingleton<IMongoDatabase>(_ => mongoClient.GetDatabase(mongoDatabaseName));
builder.Services.AddSingleton<IMongoContext, MongoContext>();

// MongoIndexInitializer is registered as a plain singleton, NOT via AddHostedService.
// It is invoked explicitly, awaited, and completed BEFORE app.Run() below — see that
// call site for why. Index creation is idempotent and stays part of the same pass.
builder.Services.AddSingleton<MongoIndexInitializer>();

// Blob Storage (MinIO)
builder.Services.AddSingleton<IBlobStorageService, MinioBlobStorageService>();
builder.Services.AddSingleton<IImageUploadService, ImageUploadService>();

// ASP.NET Identity
builder.Services.AddIdentityCore<ApplicationUser>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 8;
    options.User.RequireUniqueEmail = true;
})
.AddRoles<ApplicationRole>()
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

// FastEndpoints + JWT
builder.Services
    .AddAuthenticationJwtBearer(s =>
    {
        s.SigningKey = jwtSecret;
    })
    .AddAuthorization()
    .AddFastEndpoints()
    .SwaggerDocument(o =>
    {
        o.ShortSchemaNames = true;
        o.DocumentSettings = s =>
        {
            s.Title = "Form Up API";
            s.Version = "v1";
        };
    });

// SignalR JWT: extract token from query string for hub connections
builder.Services.AddOptions<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>(
    Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
    .Configure(options =>
    {
        options.Events ??= new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents();
        var existingOnMessageReceived = options.Events.OnMessageReceived;
        options.Events.OnMessageReceived = async context =>
        {
            if (existingOnMessageReceived is not null)
                await existingOnMessageReceived(context);

            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
            {
                context.Token = accessToken;
            }
        };
    });

// Rate Limiting
var rateLimitingDisabled = builder.Configuration.GetValue<bool>("RateLimiting:Disabled");

builder.Services.AddRateLimiter(options =>
{
    // Resolve the client IP from the request context.
    // Behind a trusted reverse proxy (Render edge), X-Forwarded-For has already
    // been resolved into HttpContext.Connection.RemoteIpAddress by UseForwardedHeaders.
    // If RemoteIpAddress is still null (should not happen in production because
    // UseForwardedHeaders is registered before UseRateLimiter), fall back to the
    // connection id — a per-connection unique string — so we never collapse all
    // anonymous connections into a single shared "unknown" bucket that one flood
    // could exhaust for everyone.
    static string GetPartitionKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString()
        ?? context.Connection.Id;

    options.AddPolicy(AppPolicies.AuthRateLimit, context =>
        rateLimitingDisabled
            ? RateLimitPartition.GetNoLimiter("disabled")
            : RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: GetPartitionKey(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(15),
                    QueueLimit = 0
                }));

    // Separate policy for the refresh endpoint so background token rotation
    // does not consume the shared login/register budget.
    // 120 permits per 15 min per IP: bounds flood abuse while comfortably
    // accommodating normal transparent refresh (multiple tabs, concurrent requests).
    options.AddPolicy(AppPolicies.RefreshRateLimit, context =>
        rateLimitingDisabled
            ? RateLimitPartition.GetNoLimiter("disabled")
            : RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: GetPartitionKey(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 120,
                    Window = TimeSpan.FromMinutes(15),
                    QueueLimit = 0
                }));

    // Partitioned by the authenticated professional's UserId, not IP — see
    // AppPolicies.PendingInviteRateLimit for why the account is the meaningful bucket here.
    // Falls back to the IP-based key only in the defensive case where the claim is somehow
    // absent (the endpoint already requires a Trainer/Nutritionist/Admin role, so this claim
    // is always present in practice).
    options.AddPolicy(AppPolicies.PendingInviteRateLimit, context =>
        rateLimitingDisabled
            ? RateLimitPartition.GetNoLimiter("disabled")
            : RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.User.FindFirst(AppClaims.UserId)?.Value ?? GetPartitionKey(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(15),
                    QueueLimit = 0
                }));

    // Live slot check in the invite drawer: same per-professional partition as the create budget,
    // but a separate bucket so typing never eats the create budget.
    options.AddPolicy(AppPolicies.PendingInviteSlotCheckRateLimit, context =>
        rateLimitingDisabled
            ? RateLimitPartition.GetNoLimiter("disabled")
            : RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.User.FindFirst(AppClaims.UserId)?.Value ?? GetPartitionKey(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(15),
                    QueueLimit = 0
                }));

    // Anonymous notification email links: see AppPolicies.EmailLinkRateLimit for why this is not the auth budget.
    options.AddPolicy(AppPolicies.EmailLinkRateLimit, context =>
        rateLimitingDisabled
            ? RateLimitPartition.GetNoLimiter("disabled")
            : RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: GetPartitionKey(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(15),
                    QueueLimit = 0
                }));

    // Anonymous push token removal: see AppPolicies.PushTokenRemoveRateLimit for why this is not the auth budget.
    options.AddPolicy(AppPolicies.PushTokenRemoveRateLimit, context =>
        rateLimitingDisabled
            ? RateLimitPartition.GetNoLimiter("disabled")
            : RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: GetPartitionKey(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(15),
                    QueueLimit = 0
                }));

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy(AppPolicies.AllowWebApp, policy =>
    {
        var origins = builder.Configuration.GetSection(ConfigKeys.CorsAllowedOrigins).Get<string[]>()
            ?? ["http://localhost:5173"];
        policy.WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// SignalR
builder.Services.AddSignalR();
builder.Services.AddSingleton<PresenceTracker>();

// Macro Calculator
builder.Services.AddSingleton<IMacroCalculatorService, MacroCalculatorService>();

// System clock — see rules/csharp-style.md#timeprovider. Not previously registered because no
// endpoint injected it yet; the meal-template feature (#859) is the first consumer.
builder.Services.AddSingleton(TimeProvider.System);

// Client link authorization (cross-DB link verification) — the single entry point every
// call site resolves professional/client link capabilities through; see #958, #964.
builder.Services.AddScoped<IClientLinkAuthorizationService, ClientLinkAuthorizationService>();
builder.Services.AddScoped<ICoachRoleStatus, CoachRoleStatus>();
builder.Services.AddScoped<ICoachDisciplineAccess, CoachDisciplineAccess>();
builder.Services.AddScoped<ICoachRoleRemover, CoachRoleRemover>();
builder.Services.AddScoped<IPrDetectionService, PrDetectionService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<INotificationEmailSender, NotificationEmailSender>();
builder.Services.AddScoped<IWorkoutCompletionService, WorkoutCompletionService>();
builder.Services.AddScoped<IEmailVerificationTokenService, EmailVerificationTokenService>();
builder.Services.AddSingleton<IEmailTurnOffTokenService, EmailTurnOffTokenService>();
// Registers IHttpClientFactory — consumed by ExpoPushNotificationService below
// and by the ResendClient typed client when Email:Provider = Resend.
builder.Services.AddHttpClient();
builder.Services.AddScoped<IPushNotificationService, ExpoPushNotificationService>();
builder.Services.AddScoped<IProfileMapperService, ProfileMapperService>();

// Email — switch provider via Email:Provider config ("Resend" or "Smtp", default: Smtp)
var emailProvider = builder.Configuration[ConfigKeys.EmailProvider] ?? "Smtp";
if (string.Equals(emailProvider, "Resend", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddOptions();
    builder.Services.AddHttpClient<ResendClient>();
    builder.Services.Configure<ResendClientOptions>(o =>
    {
        o.ApiToken = builder.Configuration[ConfigKeys.ResendApiToken]
            ?? throw new InvalidOperationException("Resend:ApiToken must be configured when Email:Provider is set to Resend.");
    });
    builder.Services.AddTransient<IResend, ResendClient>();
    builder.Services.AddScoped<IEmailService, ResendEmailService>();
}
else
{
    builder.Services.AddScoped<IEmailService, SmtpEmailService>();
}

// Realtime notifications (SignalR)
builder.Services.AddScoped<IRealtimeNotifier, SignalRNotifier>();

// Get-or-create conversation + seed-first-message (invite messages, accept-time
// statements) — shared by CreatePendingInviteEndpoint, AcceptClientInviteEndpoint,
// and AcceptInvitationEndpoint (#768).
builder.Services.AddScoped<IConversationSeedService, ConversationSeedService>();

// Seeds a professional-client conversation for a brand-new account against any
// message-bearing PendingInvite already addressed to their email — shared by
// RegisterEndpoint, GoogleSocialLoginEndpoint, and AppleSocialLoginEndpoint so the
// coach's opening message is visible before the client accepts (#803/#817).
builder.Services.AddScoped<IPendingInviteConversationSeeder, PendingInviteConversationSeeder>();
builder.Services.AddScoped<IPendingInviteNotificationSeeder, PendingInviteNotificationSeeder>();

// Photo diary reminder scheduler — registered as both singleton (for test access) and hosted service.
builder.Services.AddSingleton<PhotoDiaryReminderScheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PhotoDiaryReminderScheduler>());
builder.Services.AddSingleton<CoachRoleLinkSweeper>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CoachRoleLinkSweeper>());

// Automation worker — runs due schedule rows and polled events. Singleton (for test access, which
// calls RunDueNowAsync) and hosted service; its units of work are scoped and resolved per claim.
builder.Services.AddSingleton<AutomationScheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutomationScheduler>());
builder.Services.AddScoped<AutomationOccurrenceSender>();
builder.Services.AddScoped<AutomationClaimProcessor>();
builder.Services.AddSingleton<NotificationEmailGroupingScheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NotificationEmailGroupingScheduler>());
builder.Services.AddScoped<NotificationEmailGroupProcessor>();
builder.Services.AddScoped<AutomationReconciler>();
builder.Services.AddScoped<AutomationEventPoller>();
builder.Services.AddScoped<AutomationAnswerSweeper>();
builder.Services.AddScoped<AutomationCheckInSweeper>();

// Social login nonce reaper — periodically deletes expired/consumed nonce rows.
// Registered as singleton (for test access via IServiceProvider) and hosted service.
builder.Services.AddSingleton<SocialLoginNonceReaperService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SocialLoginNonceReaperService>());

// Unverified-account cleanup — deletes accounts that never verified their email within the retention window.
builder.Services.AddSingleton<UnverifiedAccountCleanupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UnverifiedAccountCleanupService>());

// Background email dispatch queue + worker (#702) — closes the timing-enumeration
// oracle on the anonymous resend-verification endpoint by deferring the SMTP send off
// the request path. Queue is a singleton so the request path (scoped) and the worker
// (hosted service) share the same channel. Worker registered as both singleton (for
// test access via IServiceProvider) and hosted service, mirroring the schedulers above.
builder.Services.AddSingleton<IBackgroundEmailQueue, BackgroundEmailQueue>();
builder.Services.AddSingleton<EmailDispatchWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EmailDispatchWorker>());

// Session lock service — mutual exclusion for trainer-edit vs client-live sessions
builder.Services.Configure<TrainingLockOptions>(
    builder.Configuration.GetSection(TrainingLockOptions.SectionName));
builder.Services.AddScoped<ISessionLockService, SessionLockService>();

// Client app switches (optional "ClientApp" section; defaults show everything)
builder.Services.Configure<FormUp.Application.Features.Client.GetClientAppConfig.ClientAppOptions>(
    builder.Configuration.GetSection(FormUp.Application.Features.Client.GetClientAppConfig.ClientAppOptions.SectionName));

// Compliance
builder.Services.AddScoped<IComplianceService, ComplianceService>();

// Client verdict
builder.Services.AddScoped<IClientVerdictService, ClientVerdictService>();

// Shared version-gated fetch-check-replace-409 skeleton for NutritionPlans/TrainingPlans
// mutation endpoints (Update, Publish, Complete, LinkQuestionnaire).
builder.Services.AddScoped<PlanConcurrencyGuard>();

// Resolves a coach's effective feature entitlements + client-count limit from their
// subscription (#593). No endpoint gates on this yet — see #594.
builder.Services.AddScoped<EntitlementService>();
builder.Services.AddScoped<CoachAccountDisabler>();
builder.Services.AddScoped<ClientPaymentTermsWriter>();
builder.Services.AddScoped<InvitePackageTermsApplier>();

// One-shot CLI backfill, resolved by CliCommandDispatcher — never `new`-ed
// directly. Scoped: takes IApplicationDbContext, which is scoped (see :56).
builder.Services.AddScoped<PhotoDescriptionBackfillService>();

// Google social login token verification
builder.Services.AddScoped<IGoogleTokenVerifier, GoogleTokenVerifier>();

// Apple Sign-In token verification
builder.Services.AddScoped<IAppleTokenVerifier, AppleTokenVerifier>();

// Audit
builder.Services.AddScoped<IAuditService, AuditService>();

// Exception handling
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// Health checks — `/health/live` is the cheap liveness probe (no deps),
// `/health` runs the readiness checks tagged "ready" (Postgres + Mongo ping).
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<MongoHealthCheck>("mongodb", tags: ["ready"]);

var app = builder.Build();

// Warn loudly at startup when Testing:Enabled=true.
// POST /test/reset wipes the database on every call. The Testing:Enabled flag is the
// SOLE gate — the environment name is no longer checked. This means a misconfigured
// production deploy that accidentally sets Testing:Enabled=true would expose the
// endpoint with no further backstop. The startup warning is the early alarm for
// exactly this scenario. This flag must never be set outside of test harnesses.
var testingEnabled = builder.Configuration.GetValue<bool>("Testing:Enabled");
if (testingEnabled)
{
    app.Logger.LogWarning(
        "TESTING MODE ACTIVE: POST /test/reset will wipe the database on demand. " +
        "Testing:Enabled=true is the SOLE gate — the environment name is not checked. " +
        "This flag must NEVER be set outside of isolated test harnesses. " +
        "A production deploy with this flag set has no further protection.");
}

// One-shot CLI commands (--seed, --qa-seed, --backfill-photo-descriptions,
// --drop-legacy-training-collections).
// CliCommandDispatcher.TryHandleAsync's own doc
// comment states the contract: a `true` return means one of them ran, and
// this process must exit right here — never falling through to the
// unconditional MongoIndexInitializer.StartAsync call near app.Run() below,
// nor to app.Run() itself.
if (await CliCommandDispatcher.TryHandleAsync(app, args))
{
    return;
}

// Auto-apply pending EF Core migrations — OPT-IN via Database:RunMigrationsOnStartup=true.
//
// This flag is intentionally OFF by default. It must be set explicitly in the
// environment where auto-migration is desired:
//
//   • Local dev: set in Properties/launchSettings.json (gitignored; never committed)
//         "Database__RunMigrationsOnStartup": "true"
//   • e2e compose harness: set in docker-compose.test.yml api service env section
//         Database__RunMigrationsOnStartup: "true"
//
// Render (and any other hosted environment) sets ASPNETCORE_ENVIRONMENT=Development
// for TLS / certificate reasons — that alone does NOT enable auto-migration.
// Because Render does not set Database__RunMigrationsOnStartup, the flag stays
// false and migrations are NEVER auto-applied there.
//
// NEVER promote this flag to a default-true value without explicit sign-off.
// Schema changes that affect live data must be applied intentionally with a
// reviewed migration plan.
var runMigrationsOnStartup = app.Configuration.GetValue<bool>("Database:RunMigrationsOnStartup");
if (runMigrationsOnStartup)
{
    using var migrationScope = app.Services.CreateScope();
    var migrationDb = migrationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await migrationDb.Database.MigrateAsync();
    app.Logger.LogInformation("EF Core migrations applied (Database:RunMigrationsOnStartup=true).");
}

// Middleware pipeline
app.UseExceptionHandler();

// Resolve the true client IP from X-Forwarded-For when the app runs behind
// the Render edge proxy (or any trusted reverse proxy).
//
// SECURITY: blanket trust (ForwardedHeadersOptions with no KnownProxies /
// KnownNetworks configured) lets any client spoof X-Forwarded-For and forge
// its own rate-limit partition key.  We restrict trust to:
//   • RFC 1918 private ranges (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16)
//   • IPv6 loopback / link-local (::1, fc00::/7, fe80::/10)
//
// Render's internal routing places the edge proxy on a private IP relative
// to the application container, so these ranges cover the production topology
// while preventing a public client from injecting an arbitrary forwarded IP.
//
// Must run BEFORE UseRateLimiter so the rate-limiter already sees the resolved
// IP in HttpContext.Connection.RemoteIpAddress.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor,
    // ForwardLimit defaults to 1 — assumes exactly ONE trusted hop (the Render edge proxy).
    // If the deployment topology ever adds a second trusted hop (e.g. an internal LB behind
    // Render), bump this to 2 and add the LB's network to KnownIPNetworks.
    KnownIPNetworks =
    {
        // 10.0.0.0/8  (RFC 1918)
        new System.Net.IPNetwork(System.Net.IPAddress.Parse("10.0.0.0"), 8),
        // 172.16.0.0/12  (RFC 1918)
        new System.Net.IPNetwork(System.Net.IPAddress.Parse("172.16.0.0"), 12),
        // 192.168.0.0/16  (RFC 1918)
        new System.Net.IPNetwork(System.Net.IPAddress.Parse("192.168.0.0"), 16),
        // ::1/128  (IPv6 loopback)
        new System.Net.IPNetwork(System.Net.IPAddress.IPv6Loopback, 128),
        // fc00::/7  (IPv6 unique local)
        new System.Net.IPNetwork(System.Net.IPAddress.Parse("fc00::"), 7),
        // fe80::/10  (IPv6 link-local)
        new System.Net.IPNetwork(System.Net.IPAddress.Parse("fe80::"), 10),
    }
});

// Health endpoints — registered before HTTPS redirect / auth so they remain
// reachable on plain HTTP (Render terminates TLS at the edge) and don't
// require credentials.
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false,
});
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseStaticFiles();
app.UseCors(AppPolicies.AllowWebApp);
app.UseAuthentication();
app.UseAuthorization();
// Captures Accept-Language into ApplicationUser.Language for authenticated callers
// (#788) — must run after UseAuthorization so context.User is populated.
app.UseMiddleware<LocaleCaptureMiddleware>();
app.MapHub<NotificationHub>("/hubs/notifications");
app.UseRateLimiter();
// Read-only gate after a coach role is removed (see RemovedCoachRoleAttribute); needs the resolved
// endpoint and the authenticated user, so it sits after authorization and before the endpoint runs.
app.UseMiddleware<RemovedCoachRoleMiddleware>();
// Refuses coach writes while an ended given plan awaits a plan choice; same placement and reason as the gate above.
app.UseMiddleware<FormUp.Application.Infrastructure.PlanChoice.PlanChoiceWriteGateMiddleware>();
// NOTE: ResetTestStateEndpoint is always registered in the route table.
// The single gate (Testing:Enabled=true) is enforced at request time inside
// the endpoint's HandleAsync. This avoids a process-wide static route table
// poisoning issue in test environments where multiple WebApplicationFactory
// instances share FastEndpoints' static EndpointData
// (FastEndpoints 8.x builds the route table once per process).
app.UseFastEndpoints(c =>
{
    c.Endpoints.ShortNames = true;
    c.Serializer.Options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    c.Errors.UseProblemDetails(x =>
    {
        x.IndicateErrorCode = true;
    });
});
app.UseSwaggerGen();

// Guarantee MongoIndexInitializer's idempotent index creation COMPLETES before
// Kestrel accepts any request. This is why MongoIndexInitializer is registered
// above as a plain AddSingleton, NOT AddHostedService: in the generic/web host,
// hosted services start sequentially in registration order, and the framework's
// own web-hosting service (which actually starts Kestrel listening) is registered
// ahead of anything added later in this file — so a plain
// AddHostedService<MongoIndexInitializer> would let Kestrel begin serving BEFORE
// (or concurrently with) the unique indexes existing, opening a window for a
// duplicate-key race those indexes exist to prevent. Awaiting the explicit call
// below, strictly before app.Run(), removes that race entirely: there is no
// hosted-service ordering to reason about because this is plain sequential code.
using (var migrationScope = app.Services.CreateScope())
{
    var mongoIndexInitializer = migrationScope.ServiceProvider.GetRequiredService<MongoIndexInitializer>();

    await mongoIndexInitializer.StartAsync(CancellationToken.None);
}

app.Run();
