using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Seed.WalkSeed;
using FormUp.Tests.Seed.WalkSeed;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Seed;

/// <summary>
/// Tests for <see cref="WalkSeedRunner"/>: the safety guards, and the state each account is left in after a first run
/// and after a second run that follows a walk-like mutation. The fake Stripe gateway stands in for the sandbox.
/// </summary>
[Collection("WalkSeedTests")]
public class WalkSeedRunnerTests(WalkSeedFixture fixture)
{
    [Fact]
    public void RunAsync_ProPriceWithoutStripeId_RefusesAndWritesNothing()
    {
        fixture.MissingPriceResult.Succeeded.Should().BeFalse();
        fixture.MissingPriceResult.RefusalReason.Should().Contain("Price Pro in admin first");
        fixture.MissingPriceWroteFiles.Should().BeFalse();
        fixture.MissingPriceGatewayCalls.Should().Be(0);
        fixture.WalkUsersAfterMissingPrice.Should().Be(0);
    }

    [Theory]
    [InlineData("Production", WalkSeedFixture.TestKey)]
    [InlineData("Staging", WalkSeedFixture.TestKey)]
    [InlineData("Development", null)]
    [InlineData("Development", "")]
    [InlineData("Development", "sk_live_notarealkey")]
    [InlineData("Development", "rk_test_notarealkey")]
    public async Task RunAsync_UnsafeEnvironmentOrKey_RefusesAndWritesNothing(string environmentName, string? secretKey)
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"walk-seed-guard-{Guid.NewGuid():N}");
        var gateway = new FakeWalkStripeGateway();
        var usersBefore = await fixture.CountWalkUsersAsync();

        var result = await fixture.NewRunner(environmentName, secretKey, outputRoot, gateway)
            .RunAsync(TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.RefusalReason.Should().NotBeNullOrWhiteSpace();
        result.RefusalReason.Should().NotContain("sk_live_notarealkey");
        Directory.Exists(outputRoot).Should().BeFalse();
        gateway.CallCount.Should().Be(0);
        (await fixture.CountWalkUsersAsync()).Should().Be(usersBefore);
    }

    [Fact]
    public async Task RunAsync_FirstRun_PreparesTheDatabaseOnlyAccounts()
    {
        fixture.FirstResult.Succeeded.Should().BeTrue();
        fixture.FirstResult.AccountLines.Should().HaveCount(WalkAccountCatalog.All.Count);

        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var admin = await userManager.FindByEmailAsync("walk.admin@formup.test");
        (await userManager.GetRolesAsync(admin!)).Should().BeEquivalentTo(["Admin"]);
        admin!.EmailConfirmed.Should().BeTrue();
        (await db.ProfessionalProfiles.AnyAsync(p => p.UserId == admin.Id, TestContext.Current.CancellationToken))
            .Should().BeFalse();

        var free = await ReadRowAsync(db, "walk.free@formup.test");
        free.Plan.Should().Be("free");
        free.Row.Status.Should().Be(SubscriptionStatus.Active);
        free.Row.ExternalSubscriptionId.Should().BeNull();

        var given = await ReadRowAsync(db, "walk.given@formup.test");
        given.Plan.Should().Be("pro");
        given.Row.Status.Should().Be(SubscriptionStatus.Active);
        given.Row.GivenFreeAt.Should().NotBeNull();
        given.Row.FreeUntil.Should().BeNull();
        given.Row.ExternalCustomerId.Should().BeNull();
        given.Row.ExternalSubscriptionId.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_FirstRun_CreatesStripeSubscriptionsForTheThreePayingCoaches()
    {
        fixture.Gateway.Created.Take(fixture.CreatedAfterFirstRun).Should().BeEquivalentTo(
            [
                ("pro", WalkSeedFixture.ProExternalPriceId, false),
                ("pro3ds", WalkSeedFixture.ProExternalPriceId, true),
                ("manyclients", WalkSeedFixture.ProExternalPriceId, false),
            ],
            options => options.WithStrictOrdering());

        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        foreach (var email in new[] { "walk.pro@formup.test", "walk.pro3ds@formup.test", "walk.manyclients@formup.test" })
        {
            var paying = await ReadRowAsync(db, email);
            paying.Plan.Should().Be("pro");
            paying.Row.Status.Should().Be(SubscriptionStatus.Active);
            paying.Row.ExternalCustomerId.Should().StartWith("cus_walk_");
            paying.Row.ExternalSubscriptionId.Should().StartWith("sub_walk_");
            paying.Row.CurrentPeriodEndsAt.Should().NotBeNull();
            paying.Row.FirstPaidAt.Should().NotBeNull();
            paying.Row.LastExternalEventAt.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task RunAsync_SecondRun_LeavesOneUserPerEmailAndResetsEveryAccount()
    {
        fixture.SecondResult.Succeeded.Should().BeTrue();
        (await fixture.CountWalkUsersAsync()).Should().Be(WalkAccountCatalog.AllEmails().Count);

        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        foreach (var email in WalkAccountCatalog.AllEmails())
        {
            (await db.Users.CountAsync(
                user => user.NormalizedEmail == email.ToUpperInvariant(), TestContext.Current.CancellationToken))
                .Should().Be(1, email);
        }

        var threeDsUser = await userManager.FindByEmailAsync("walk.pro3ds@formup.test");
        (await userManager.GetRolesAsync(threeDsUser!)).Should().BeEquivalentTo(["Trainer"]);

        var threeDs = await ReadRowAsync(db, "walk.pro3ds@formup.test");
        var profile = await db.ProfessionalProfiles.SingleAsync(
            p => p.UserId == threeDsUser!.Id, TestContext.Current.CancellationToken);
        profile.NutritionistRoleRemovedAt.Should().BeNull();
        profile.TrainerRoleRemovedAt.Should().BeNull();
        profile.CoachAccountActiveUntil.Should().BeNull();

        threeDs.Row.PaysSecondRoleAddOn.Should().BeFalse();
        threeDs.Row.SecondRoleAddOnEndsAt.Should().BeNull();
        threeDs.Row.PendingSubscriptionPlanPriceId.Should().BeNull();
        threeDs.Row.CancelAtPeriodEnd.Should().BeFalse();
        threeDs.Row.RenewalCancelledByAdminAt.Should().BeNull();
        threeDs.Row.NextPaymentAttemptAt.Should().BeNull();
        threeDs.Row.TrialEndsAt.Should().BeNull();
        threeDs.Row.CurrentPromotionId.Should().BeNull();
        threeDs.Row.Status.Should().Be(SubscriptionStatus.Active);
    }

    [Fact]
    public async Task RunAsync_SecondRun_RetiresThePreviousSubscriptionsAfterTheRowLetGoOfThem()
    {
        var retired = fixture.Gateway.Retired;
        retired.Select(entry => entry.SubscriptionId).Should().BeEquivalentTo(["sub_walk_1", "sub_walk_2", "sub_walk_3"]);

        // At retire time no row held the old subscription id any more, so a late deleted event finds nothing to rewrite.
        fixture.SubscriptionRowsHoldingRetiredIds.Should().Equal(0, 0, 0);

        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var pro = await ReadRowAsync(db, "walk.pro@formup.test");
        pro.Row.ExternalSubscriptionId.Should().Be("sub_walk_4");
        pro.Row.LastExternalEventAt.Should().BeOnOrAfter(fixture.ProEventAtAfterFirstRun!.Value);
    }

    [Fact]
    public async Task RunAsync_SecondRun_ChangesPasswordsAndKeepsOneActiveRefreshTokenPerAccount()
    {
        var firstProPassword = ReadPassword(fixture.FirstAccountsFile, "pro");
        var secondProPassword = ReadPassword(fixture.SecondAccountsFile, "pro");
        secondProPassword.Should().NotBe(firstProPassword);
        secondProPassword.Length.Should().BeGreaterThanOrEqualTo(24);

        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var pro = await userManager.FindByEmailAsync("walk.pro@formup.test");
        (await userManager.CheckPasswordAsync(pro!, secondProPassword)).Should().BeTrue();
        (await userManager.CheckPasswordAsync(pro!, firstProPassword)).Should().BeFalse();

        foreach (var account in WalkAccountCatalog.All)
        {
            var user = await userManager.FindByEmailAsync(account.Email);
            var active = await db.RefreshTokens
                .CountAsync(token => token.UserId == user!.Id && token.RevokedAt == null, TestContext.Current.CancellationToken);
            active.Should().Be(1, account.Key);
        }

        fixture.FirstResult.AccountLines.Concat(fixture.SecondResult.AccountLines)
            .Should().NotContain(line => line.Contains(firstProPassword) || line.Contains(secondProPassword));
    }

    [Fact]
    public async Task RunAsync_SecondRun_LeavesTheManyClientsCoachWithExactlyThreeConnectedLinks()
    {
        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var manyClientsProfileId = await db.ProfessionalProfiles
            .Where(p => p.User.Email == "walk.manyclients@formup.test")
            .Select(p => p.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        var connected = await db.ClientProfessionalLinks
            .Where(l => l.ProfessionalProfileId == manyClientsProfileId && l.ConnectionState == ConnectionState.Connected)
            .Select(l => l.ClientProfile.User.Email)
            .ToListAsync(TestContext.Current.CancellationToken);

        connected.Should().BeEquivalentTo(
            Enumerable.Range(1, WalkAccountCatalog.ManyClientsClientCount).Select(WalkAccountCatalog.ManyClientsClientEmail));

        var extraLink = await db.ClientProfessionalLinks.SingleAsync(
            l => l.ProfessionalProfileId == manyClientsProfileId
                 && l.ClientProfile.PublicId == fixture.ExtraLinkedClientProfilePublicId,
            TestContext.Current.CancellationToken);
        extraLink.ConnectionState.Should().Be(ConnectionState.Ended);
    }

    [Fact]
    public async Task RunAsync_SecondRun_PutsKeptAndReleasedLapseLinksBackToConnected()
    {
        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var freeLinkStates = await db.ClientProfessionalLinks
            .Where(l => l.ProfessionalProfile.User.Email == "walk.free@formup.test")
            .Select(l => l.ConnectionState)
            .ToListAsync(TestContext.Current.CancellationToken);

        freeLinkStates.Should().NotBeEmpty();
        freeLinkStates.Should().OnlyContain(state => state == ConnectionState.Connected);
    }

    [Fact]
    public async Task RunAsync_SecondRun_WritesSessionFilesWithTheMintedRefreshToken()
    {
        var sessionsFolder = new WalkSeedOptions(fixture.OutputRoot, WalkSeedOptions.DefaultWebOrigin).SessionsFolderPath;

        Directory.GetFiles(sessionsFolder).Select(Path.GetFileName).Should().BeEquivalentTo(
            WalkAccountCatalog.All.Select(account => $"{account.Key}.json"));

        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        foreach (var account in WalkAccountCatalog.All)
        {
            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(sessionsFolder, $"{account.Key}.json"), TestContext.Current.CancellationToken));

            document.RootElement.GetProperty("cookies").GetArrayLength().Should().Be(0);
            var origin = document.RootElement.GetProperty("origins").EnumerateArray().Single();
            origin.GetProperty("origin").GetString().Should().Be("http://localhost:5173");
            var entry = origin.GetProperty("localStorage").EnumerateArray().Single();
            entry.GetProperty("name").GetString().Should().Be("refreshToken");

            var token = entry.GetProperty("value").GetString();
            Convert.FromBase64String(token!).Length.Should().Be(64);

            var stored = await db.RefreshTokens.Include(row => row.User).SingleAsync(
                row => row.Token == token, TestContext.Current.CancellationToken);
            stored.IsActive.Should().BeTrue();
            stored.User.Email.Should().Be(account.Email);
        }
    }

    [Fact]
    public void RunAsync_SecondRun_WritesTheAccountsFileInTheDocumentedShape()
    {
        using var document = JsonDocument.Parse(fixture.SecondAccountsFile);
        var root = document.RootElement;

        root.GetProperty("admin").GetProperty("email").GetString().Should().Be("walk.admin@formup.test");
        root.GetProperty("admin").GetProperty("password").GetString().Should().NotBeNullOrEmpty();

        var coaches = root.GetProperty("coaches").EnumerateArray().ToList();
        coaches.Select(coach => coach.GetProperty("key").GetString())
            .Should().Equal("free", "pro", "pro3ds", "given", "manyclients");

        foreach (var coach in coaches)
        {
            coach.GetProperty("email").GetString().Should().EndWith("@formup.test");
            coach.GetProperty("password").GetString().Should().NotBeNullOrEmpty();
            coach.GetProperty("note").GetString().Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void FromEnvironment_ContentRootInsideAWorktree_FindsTheDirectoryWithAGitFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"walk-root-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "backend", "FormUp.Application");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: /elsewhere");

        try
        {
            var options = WalkSeedOptions.FromEnvironment(nested);

            options.Should().NotBeNull();
            options!.OutputRoot.Should().Be(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(string Plan, CoachSubscription Row)> ReadRowAsync(ApplicationDbContext db, string coachEmail)
    {
        var row = await db.CoachSubscriptions
            .Include(cs => cs.SubscriptionPlan)
            .SingleAsync(cs => cs.ProfessionalProfile.User.Email == coachEmail);

        return (row.SubscriptionPlan.Code, row);
    }

    private static string ReadPassword(string accountsFile, string coachKey)
    {
        using var document = JsonDocument.Parse(accountsFile);

        return document.RootElement.GetProperty("coaches").EnumerateArray()
            .Single(coach => coach.GetProperty("key").GetString() == coachKey)
            .GetProperty("password").GetString()!;
    }
}
