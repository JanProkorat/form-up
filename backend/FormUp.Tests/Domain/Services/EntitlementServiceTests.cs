using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Domain.Services;

/// <summary>
/// Testcontainers coverage for <see cref="EntitlementService"/> — resolves a coach's effective
/// feature entitlements and client-count limit from their <see cref="CoachSubscription"/> (#593).
/// </summary>
[Collection(TestCollection.Name)]
public class EntitlementServiceTests(FitnessApiFactory factory)
{
    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@entitlement-service-{tag}.com";

    private async Task<Guid> RegisterProfessionalAsync(string tag)
    {
        var client = factory.CreateClient();
        var email = UniqueEmail(tag);
        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Test", "Professional", "Trainer");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
        return user.Id;
    }

    private async Task<long> GetProfessionalProfileIdAsync(Guid professionalUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.ProfessionalProfiles.FirstAsync(
            pp => pp.UserId == professionalUserId, TestContext.Current.CancellationToken);
        return profile.Id;
    }

    private async Task CreateClientLinkAsync(Guid professionalUserId, string tag, bool isActive = true)
    {
        var client = factory.CreateClient();
        var email = UniqueEmail(tag);
        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Test", "Client", "Client");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clientUser = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
        var clientProfile = await db.ClientProfiles.FirstAsync(
            cp => cp.UserId == clientUser.Id, TestContext.Current.CancellationToken);
        var professionalProfile = await db.ProfessionalProfiles.FirstAsync(
            pp => pp.UserId == professionalUserId, TestContext.Current.CancellationToken);

        db.ClientProfessionalLinks.Add(new ClientProfessionalLink
        {
            PublicId = Guid.NewGuid(),
            ProfessionalProfileId = professionalProfile.Id,
            ClientProfileId = clientProfile.Id,
            ProfessionalRole = UserRole.Trainer,
            ConnectionState = isActive ? ConnectionState.Connected : ConnectionState.Ended,
            CanViewNutritionPlans = true,
            CanViewTrainingPlans = true
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<long> CreateSubscriptionPlanAsync(
        string tag,
        int? maxActiveClients = null,
        bool canCreatePlans = true,
        bool canMessage = true,
        bool canSendQuestionnaires = true,
        bool canUseWeeklyCheckIns = true,
        bool canUsePerClientCheckInConfig = true,
        bool canUsePayments = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var plan = new SubscriptionPlan
        {
            Code = $"plan-{tag}-{Guid.NewGuid():N}",
            NameCs = "Plán",
            NameEn = "Plan",
            NameDe = "Plan",
            BothRolesIncluded = true,
            CanCreatePlans = canCreatePlans,
            CanMessage = canMessage,
            CanSendQuestionnaires = canSendQuestionnaires,
            CanUseWeeklyCheckIns = canUseWeeklyCheckIns,
            CanUsePerClientCheckInConfig = canUsePerClientCheckInConfig,
            CanUsePayments = canUsePayments,
            MaxActiveClients = maxActiveClients,
            PriceMinorUnits = 99900,
            Currency = "CZK",
            BillingInterval = BillingInterval.Monthly,
            IsActive = true
        };

        SubscriptionPlanPriceBuilder.OpenPriceFor(plan);
        db.SubscriptionPlans.Add(plan);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return plan.Id;
    }

    private async Task CreateCoachSubscriptionAsync(long professionalProfileId, long subscriptionPlanId, SubscriptionStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var priceId = await db.SubscriptionPlanPrices
            .Where(p => p.SubscriptionPlanId == subscriptionPlanId && p.ValidTo == null)
            .Select(p => p.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        db.CoachSubscriptions.Add(new CoachSubscription
        {
            ProfessionalProfileId = professionalProfileId,
            SubscriptionPlanId = subscriptionPlanId,
            SubscriptionPlanPriceId = priceId,
            Status = status
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private EntitlementService CreateService(out IServiceScope scope)
    {
        scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<EntitlementService>();
    }

    // ── GetEntitlementsAsync — status axis ──────────────────────────────────────

    [Theory]
    [InlineData(SubscriptionStatus.Trialing, true)]
    [InlineData(SubscriptionStatus.Active, true)]
    [InlineData(SubscriptionStatus.PastDue, true)]
    [InlineData(SubscriptionStatus.Canceled, false)]
    [InlineData(SubscriptionStatus.Incomplete, false)]
    public async Task GetEntitlementsAsync_EveryStatus_ResolvesPlanFlagsOrFallsBackToFree(
        SubscriptionStatus status, bool expectPlanFlags)
    {
        var professionalUserId = await RegisterProfessionalAsync($"status-{status}");
        var professionalProfileId = await GetProfessionalProfileIdAsync(professionalUserId);
        var planId = await CreateSubscriptionPlanAsync($"status-{status}");
        await CreateCoachSubscriptionAsync(professionalProfileId, planId, status);
        var service = CreateService(out var scope);

        var entitlements = await service.GetEntitlementsAsync(professionalProfileId, TestContext.Current.CancellationToken);

        // The fixture plan switches every flag on; the seeded Free plan keeps the paid-only ones off.
        entitlements.CanSendQuestionnaires.Should().Be(expectPlanFlags);
        entitlements.CanUseWeeklyCheckIns.Should().Be(expectPlanFlags);
        entitlements.CanUsePerClientCheckInConfig.Should().Be(expectPlanFlags);
        scope.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetEntitlementsAsync_ActiveStatus_CopiesCanUsePaymentsFromPlan(bool canUsePayments)
    {
        var professionalUserId = await RegisterProfessionalAsync($"payments-{canUsePayments}");
        var professionalProfileId = await GetProfessionalProfileIdAsync(professionalUserId);
        var planId = await CreateSubscriptionPlanAsync($"payments-{canUsePayments}", canUsePayments: canUsePayments);
        await CreateCoachSubscriptionAsync(professionalProfileId, planId, SubscriptionStatus.Active);
        var service = CreateService(out var scope);

        var entitlements = await service.GetEntitlementsAsync(professionalProfileId, TestContext.Current.CancellationToken);

        entitlements.CanUsePayments.Should().Be(canUsePayments);
        scope.Dispose();
    }

    // ── GetEntitlementsAsync — MaxActiveClients passthrough ─────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData(5)]
    public async Task GetEntitlementsAsync_ActiveStatus_CopiesMaxActiveClientsFromPlan(int? maxActiveClients)
    {
        var professionalUserId = await RegisterProfessionalAsync($"maxclients-{maxActiveClients}");
        var professionalProfileId = await GetProfessionalProfileIdAsync(professionalUserId);
        var planId = await CreateSubscriptionPlanAsync($"maxclients-{maxActiveClients}", maxActiveClients: maxActiveClients);
        await CreateCoachSubscriptionAsync(professionalProfileId, planId, SubscriptionStatus.Active);
        var service = CreateService(out var scope);

        var entitlements = await service.GetEntitlementsAsync(professionalProfileId, TestContext.Current.CancellationToken);

        entitlements.MaxActiveClients.Should().Be(maxActiveClients);
        scope.Dispose();
    }

    // ── No CoachSubscription row — the Free plan applies ────────────────────────

    [Fact]
    public async Task GetEntitlementsAsync_NoSubscriptionRow_ReturnsTheFreePlanEntitlements()
    {
        var professionalUserId = await RegisterProfessionalAsync("no-subscription");
        var professionalProfileId = await GetProfessionalProfileIdAsync(professionalUserId);
        var service = CreateService(out var scope);
        var free = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().SubscriptionPlans
            .AsNoTracking()
            .SingleAsync(p => p.Code == "free", TestContext.Current.CancellationToken);

        var entitlements = await service.GetEntitlementsAsync(professionalProfileId, TestContext.Current.CancellationToken);

        entitlements.Should().Be(new CoachEntitlements(
            free.CanCreatePlans, free.CanMessage, free.CanSendQuestionnaires, free.CanUseWeeklyCheckIns,
            free.CanUsePerClientCheckInConfig, free.CanUsePayments, free.MaxActiveClients));
        scope.Dispose();
    }

    [Fact]
    public async Task GetActiveClientCountAsync_NoSubscriptionRow_ReturnsRealCount()
    {
        var professionalUserId = await RegisterProfessionalAsync("no-sub-count");
        var professionalProfileId = await GetProfessionalProfileIdAsync(professionalUserId);
        await CreateClientLinkAsync(professionalUserId, "no-sub-count-1");
        await CreateClientLinkAsync(professionalUserId, "no-sub-count-2");
        var service = CreateService(out var scope);

        var count = await service.GetActiveClientCountAsync(professionalProfileId, TestContext.Current.CancellationToken);

        count.Should().Be(2);
        scope.Dispose();
    }

    [Fact]
    public async Task CanAddClientAsync_NoSubscriptionRowAtTheFreeCap_ReturnsFalse()
    {
        var professionalUserId = await RegisterProfessionalAsync("no-sub-add");
        var professionalProfileId = await GetProfessionalProfileIdAsync(professionalUserId);
        var service = CreateService(out var scope);
        var free = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().SubscriptionPlans
            .AsNoTracking()
            .SingleAsync(p => p.Code == "free", TestContext.Current.CancellationToken);

        for (var index = 0; index < free.MaxActiveClients!.Value; index++)
        {
            await CreateClientLinkAsync(professionalUserId, $"no-sub-add-{index}");
        }

        var canAdd = await service.CanAddClientAsync(professionalProfileId, TestContext.Current.CancellationToken);

        canAdd.Should().BeFalse("no subscription row resolves to the Free plan and its client cap");
        scope.Dispose();
    }

    // ── GetActiveClientCountAsync — counts only active links ────────────────────

    [Fact]
    public async Task GetActiveClientCountAsync_InactiveLink_IsExcludedFromCount()
    {
        var professionalUserId = await RegisterProfessionalAsync("count-excludes-inactive");
        var professionalProfileId = await GetProfessionalProfileIdAsync(professionalUserId);
        await CreateClientLinkAsync(professionalUserId, "count-excludes-inactive-active", isActive: true);
        await CreateClientLinkAsync(professionalUserId, "count-excludes-inactive-inactive", isActive: false);
        var service = CreateService(out var scope);

        var count = await service.GetActiveClientCountAsync(professionalProfileId, TestContext.Current.CancellationToken);

        count.Should().Be(1);
        scope.Dispose();
    }

    // ── CanAddClientAsync — client-cap boundary ──────────────────────────────────

    [Fact]
    public async Task CanAddClientAsync_ActiveCountBelowCap_ReturnsTrue()
    {
        var professionalUserId = await RegisterProfessionalAsync("cap-below");
        var professionalProfileId = await GetProfessionalProfileIdAsync(professionalUserId);
        var planId = await CreateSubscriptionPlanAsync("cap-below", maxActiveClients: 3);
        await CreateCoachSubscriptionAsync(professionalProfileId, planId, SubscriptionStatus.Active);
        await CreateClientLinkAsync(professionalUserId, "cap-below-1");
        var service = CreateService(out var scope);

        var canAdd = await service.CanAddClientAsync(professionalProfileId, TestContext.Current.CancellationToken);

        canAdd.Should().BeTrue();
        scope.Dispose();
    }

    [Fact]
    public async Task CanAddClientAsync_ActiveCountExactlyAtCap_ReturnsFalse()
    {
        var professionalUserId = await RegisterProfessionalAsync("cap-exact");
        var professionalProfileId = await GetProfessionalProfileIdAsync(professionalUserId);
        var planId = await CreateSubscriptionPlanAsync("cap-exact", maxActiveClients: 2);
        await CreateCoachSubscriptionAsync(professionalProfileId, planId, SubscriptionStatus.Active);
        await CreateClientLinkAsync(professionalUserId, "cap-exact-1");
        await CreateClientLinkAsync(professionalUserId, "cap-exact-2");
        var service = CreateService(out var scope);

        var canAdd = await service.CanAddClientAsync(professionalProfileId, TestContext.Current.CancellationToken);

        canAdd.Should().BeFalse("the cap is inclusive of the current count, so being exactly at the cap blocks another add");
        scope.Dispose();
    }

    [Fact]
    public async Task CanAddClientAsync_ActiveCountAboveCap_ReturnsFalse()
    {
        var professionalUserId = await RegisterProfessionalAsync("cap-above");
        var professionalProfileId = await GetProfessionalProfileIdAsync(professionalUserId);
        // The cap is created lower than the client count that already exists — simulates a
        // plan downgrade after clients were already linked.
        await CreateClientLinkAsync(professionalUserId, "cap-above-1");
        await CreateClientLinkAsync(professionalUserId, "cap-above-2");
        await CreateClientLinkAsync(professionalUserId, "cap-above-3");
        var planId = await CreateSubscriptionPlanAsync("cap-above", maxActiveClients: 1);
        await CreateCoachSubscriptionAsync(professionalProfileId, planId, SubscriptionStatus.Active);
        var service = CreateService(out var scope);

        var canAdd = await service.CanAddClientAsync(professionalProfileId, TestContext.Current.CancellationToken);

        canAdd.Should().BeFalse();
        scope.Dispose();
    }

    [Fact]
    public async Task CanAddClientAsync_NullCap_ReturnsTrueRegardlessOfClientCount()
    {
        var professionalUserId = await RegisterProfessionalAsync("cap-null");
        var professionalProfileId = await GetProfessionalProfileIdAsync(professionalUserId);
        var planId = await CreateSubscriptionPlanAsync("cap-null", maxActiveClients: null);
        await CreateCoachSubscriptionAsync(professionalProfileId, planId, SubscriptionStatus.Active);
        await CreateClientLinkAsync(professionalUserId, "cap-null-1");
        await CreateClientLinkAsync(professionalUserId, "cap-null-2");
        await CreateClientLinkAsync(professionalUserId, "cap-null-3");
        var service = CreateService(out var scope);

        var canAdd = await service.CanAddClientAsync(professionalProfileId, TestContext.Current.CancellationToken);

        canAdd.Should().BeTrue();
        scope.Dispose();
    }
}
