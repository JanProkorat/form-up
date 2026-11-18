using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Seed.WalkSeed;
using FormUp.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Seed.WalkSeed;

/// <summary>
/// One Postgres database with the walk seed run twice over it: a first run, then a run after a walk-like mutation of
/// the accounts. The facts only read what these two runs left behind (CI time budget: never seed per fact).
/// </summary>
public sealed class WalkSeedFixture(SharedTestContainers sharedContainers) : IAsyncLifetime
{
    internal const string TestKey = "sk_test_fixture";
    internal const string ProExternalPriceId = "price_walk_pro";

    private ServiceProvider? provider;

    internal ServiceProvider Provider => provider!;

    internal FakeWalkStripeGateway Gateway { get; } = new();

    internal string OutputRoot { get; } = Path.Combine(Path.GetTempPath(), $"walk-seed-{Guid.NewGuid():N}");

    internal WalkSeedResult MissingPriceResult { get; private set; } = null!;

    internal bool MissingPriceWroteFiles { get; private set; }

    internal int MissingPriceGatewayCalls { get; private set; }

    internal int WalkUsersAfterMissingPrice { get; private set; }

    internal WalkSeedResult FirstResult { get; private set; } = null!;

    internal WalkSeedResult SecondResult { get; private set; } = null!;

    internal string FirstAccountsFile { get; private set; } = string.Empty;

    internal string SecondAccountsFile { get; private set; } = string.Empty;

    internal DateTimeOffset? ProEventAtAfterFirstRun { get; private set; }

    internal int CreatedAfterFirstRun { get; private set; }

    internal List<int> SubscriptionRowsHoldingRetiredIds { get; } = [];

    internal Guid ExtraLinkedClientProfilePublicId { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var connectionString = await sharedContainers.CreatePostgresDatabaseAsync("walk_seed");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention()
                .ConfigureWarnings(warnings => warnings.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredLength = 8;
                options.User.RequireUniqueEmail = true;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();
        provider = services.BuildServiceProvider();

        await ApplicationDbContextSeed.SeedAsync(provider);

        // The seeded Pro price has no Stripe price id yet: the run must refuse and write nothing.
        MissingPriceResult = await NewRunner("Development", TestKey, OutputRoot, Gateway).RunAsync(CancellationToken.None);
        MissingPriceWroteFiles = Directory.Exists(OutputRoot);
        MissingPriceGatewayCalls = Gateway.CallCount;
        WalkUsersAfterMissingPrice = await CountWalkUsersAsync();

        await GiveProAStripePriceAsync();

        Gateway.BeforeRetire = async (_, subscriptionId) =>
            SubscriptionRowsHoldingRetiredIds.Add(await CountRowsHoldingSubscriptionAsync(subscriptionId));

        FirstResult = await NewRunner("Development", TestKey, OutputRoot, Gateway).RunAsync(CancellationToken.None);
        FirstAccountsFile = await File.ReadAllTextAsync(AccountsFilePath);
        CreatedAfterFirstRun = Gateway.Created.Count;
        ProEventAtAfterFirstRun = await ReadSubscriptionEventAtAsync("walk.pro@formup.test");

        await MutateLikeAWalkAsync();

        SecondResult = await NewRunner("Development", TestKey, OutputRoot, Gateway).RunAsync(CancellationToken.None);
        SecondAccountsFile = await File.ReadAllTextAsync(AccountsFilePath);
    }

    public async ValueTask DisposeAsync()
    {
        if (provider is not null)
        {
            await provider.DisposeAsync();
        }

        if (Directory.Exists(OutputRoot))
        {
            Directory.Delete(OutputRoot, recursive: true);
        }
    }

    internal string AccountsFilePath => new WalkSeedOptions(OutputRoot, WalkSeedOptions.DefaultWebOrigin).AccountsFilePath;

    internal WalkSeedRunner NewRunner(
        string environmentName, string? stripeSecretKey, string outputRoot, IWalkStripeGateway gateway) =>
        new(
            Provider,
            () => gateway,
            environmentName,
            stripeSecretKey,
            new WalkSeedOptions(outputRoot, WalkSeedOptions.DefaultWebOrigin),
            TimeProvider.System);

    internal async Task<int> CountWalkUsersAsync()
    {
        using var scope = Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var emails = WalkAccountCatalog.AllEmails().Select(email => email.ToUpperInvariant()).ToList();

        return await db.Users.CountAsync(user => emails.Contains(user.NormalizedEmail!));
    }

    private async Task GiveProAStripePriceAsync()
    {
        using var scope = Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var price = await db.SubscriptionPlanPrices
            .SingleAsync(p => p.SubscriptionPlan.Code == "pro" && p.ValidTo == null);
        price.ExternalPriceId = ProExternalPriceId;
        await db.SaveChangesAsync();
    }

    private async Task<int> CountRowsHoldingSubscriptionAsync(string? subscriptionId)
    {
        using var scope = Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.CoachSubscriptions.CountAsync(row => row.ExternalSubscriptionId == subscriptionId);
    }

    private async Task<DateTimeOffset?> ReadSubscriptionEventAtAsync(string coachEmail)
    {
        using var scope = Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.CoachSubscriptions
            .Where(row => row.ProfessionalProfile.User.Email == coachEmail)
            .Select(row => row.LastExternalEventAt)
            .SingleAsync();
    }

    /// <summary>
    /// Does what a walk does to the accounts: Add role on the 3D Secure coach, a scheduled change and an add-on on the
    /// Pro row, a kept link on the Free coach, and one more client linked to the many-clients coach.
    /// </summary>
    private async Task MutateLikeAWalkAsync()
    {
        using var scope = Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var now = DateTimeOffset.UtcNow;

        var threeDsUser = await userManager.FindByEmailAsync("walk.pro3ds@formup.test");
        await userManager.AddToRoleAsync(threeDsUser!, UserRole.Nutritionist.ToString());

        var threeDsProfile = await db.ProfessionalProfiles.SingleAsync(p => p.UserId == threeDsUser!.Id);
        threeDsProfile.NutritionistRoleRemovedAt = now.UtcDateTime;
        threeDsProfile.CoachAccountActiveUntil = now.UtcDateTime.AddDays(3);

        var freePriceId = await db.SubscriptionPlanPrices
            .Where(p => p.SubscriptionPlan.Code == "free" && p.ValidTo == null)
            .Select(p => p.Id)
            .SingleAsync();

        var threeDsRow = await db.CoachSubscriptions.SingleAsync(row => row.ProfessionalProfileId == threeDsProfile.Id);
        threeDsRow.PaysSecondRoleAddOn = true;
        threeDsRow.SecondRoleAddOnEndsAt = now.AddDays(10);
        threeDsRow.PendingSubscriptionPlanPriceId = freePriceId;
        threeDsRow.CancelAtPeriodEnd = true;
        threeDsRow.RenewalCancelledByAdminAt = now;
        threeDsRow.NextPaymentAttemptAt = now.AddDays(2);
        threeDsRow.TrialEndsAt = now.AddDays(5);

        var freeUser = await userManager.FindByEmailAsync("walk.free@formup.test");
        var freeProfile = await db.ProfessionalProfiles.SingleAsync(p => p.UserId == freeUser!.Id);
        var manyClientsUser = await userManager.FindByEmailAsync("walk.manyclients@formup.test");
        var manyClientsProfile = await db.ProfessionalProfiles.SingleAsync(p => p.UserId == manyClientsUser!.Id);

        var extraUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "walk.extra.client@formup.test",
            Email = "walk.extra.client@formup.test",
            EmailConfirmed = true,
            FirstName = "Extra",
            LastName = "Client",
        };
        await userManager.CreateAsync(extraUser);
        var extraProfile = new ClientProfile { UserId = extraUser.Id, PublicId = Guid.NewGuid(), IsOnboardingComplete = true };
        db.ClientProfiles.Add(extraProfile);
        await db.SaveChangesAsync();
        ExtraLinkedClientProfilePublicId = extraProfile.PublicId;

        db.ClientProfessionalLinks.Add(new ClientProfessionalLink
        {
            ProfessionalProfileId = manyClientsProfile.Id,
            ClientProfileId = extraProfile.Id,
            PublicId = Guid.NewGuid(),
            ProfessionalRole = UserRole.Trainer,
            ConnectionState = ConnectionState.Connected,
        });
        db.ClientProfessionalLinks.Add(new ClientProfessionalLink
        {
            ProfessionalProfileId = freeProfile.Id,
            ClientProfileId = extraProfile.Id,
            PublicId = Guid.NewGuid(),
            ProfessionalRole = UserRole.Trainer,
            ConnectionState = ConnectionState.FreePlanKept,
        });

        await db.SaveChangesAsync();
    }
}
