using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Domain.Services;

/// <summary>
/// Testcontainers coverage for <see cref="IClientLinkAuthorizationService"/> — the single entry
/// point every call site resolves link authorization through (#958).
/// </summary>
[Collection(TestCollection.Name)]
public class ClientLinkAuthorizationServiceTests(FitnessApiFactory factory)
{
    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@link-auth-service-{tag}.com";

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

    private async Task<(Guid ClientPublicId, Guid ClientUserId)> RegisterClientAsync(string tag)
    {
        var client = factory.CreateClient();
        var email = UniqueEmail(tag);
        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Test", "Client", "Client");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
        var profile = await db.ClientProfiles.FirstAsync(
            cp => cp.UserId == user.Id, TestContext.Current.CancellationToken);
        return (profile.PublicId, user.Id);
    }

    private async Task CreateLinkAsync(
        Guid professionalUserId, Guid clientUserId, bool isActive, bool canViewNutritionPlans, bool canViewTrainingPlans)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var professionalProfile = await db.ProfessionalProfiles.FirstAsync(
            pp => pp.UserId == professionalUserId, TestContext.Current.CancellationToken);
        var clientProfile = await db.ClientProfiles.FirstAsync(
            cp => cp.UserId == clientUserId, TestContext.Current.CancellationToken);

        db.ClientProfessionalLinks.Add(new ClientProfessionalLink
        {
            PublicId = Guid.NewGuid(),
            ProfessionalProfileId = professionalProfile.Id,
            ClientProfileId = clientProfile.Id,
            ProfessionalRole = UserRole.Trainer,
            ConnectionState = isActive ? ConnectionState.Connected : ConnectionState.Ended,
            CanViewNutritionPlans = canViewNutritionPlans,
            CanViewTrainingPlans = canViewTrainingPlans,
            DateCreated = DateTime.UtcNow
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private IClientLinkAuthorizationService CreateService(out IServiceScope scope)
    {
        scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IClientLinkAuthorizationService>();
    }

    // ── GetCapabilitiesByClientPublicIdAsync — null vs GrantsNothing ────────────

    [Fact]
    public async Task GetCapabilitiesByClientPublicIdAsync_NoProfessionalProfile_ReturnsNull()
    {
        var (clientPublicId, _) = await RegisterClientAsync("pub-no-prof");
        var service = CreateService(out var scope);

        var capabilities = await service.GetCapabilitiesByClientPublicIdAsync(
            Guid.NewGuid(), clientPublicId, TestContext.Current.CancellationToken);

        capabilities.Should().BeNull();
        scope.Dispose();
    }

    [Fact]
    public async Task GetCapabilitiesByClientPublicIdAsync_NoClientProfile_ReturnsNull()
    {
        var professionalUserId = await RegisterProfessionalAsync("pub-no-client");
        var service = CreateService(out var scope);

        var capabilities = await service.GetCapabilitiesByClientPublicIdAsync(
            professionalUserId, Guid.NewGuid(), TestContext.Current.CancellationToken);

        capabilities.Should().BeNull();
        scope.Dispose();
    }

    [Fact]
    public async Task GetCapabilitiesByClientPublicIdAsync_NoLink_ReturnsNull()
    {
        var professionalUserId = await RegisterProfessionalAsync("pub-no-link");
        var (clientPublicId, _) = await RegisterClientAsync("pub-no-link");
        var service = CreateService(out var scope);

        var capabilities = await service.GetCapabilitiesByClientPublicIdAsync(
            professionalUserId, clientPublicId, TestContext.Current.CancellationToken);

        capabilities.Should().BeNull();
        scope.Dispose();
    }

    [Fact]
    public async Task GetCapabilitiesByClientPublicIdAsync_InactiveLink_ReturnsNull()
    {
        var professionalUserId = await RegisterProfessionalAsync("pub-inactive");
        var (clientPublicId, clientUserId) = await RegisterClientAsync("pub-inactive");
        await CreateLinkAsync(professionalUserId, clientUserId, isActive: false, canViewNutritionPlans: true, canViewTrainingPlans: true);
        var service = CreateService(out var scope);

        var capabilities = await service.GetCapabilitiesByClientPublicIdAsync(
            professionalUserId, clientPublicId, TestContext.Current.CancellationToken);

        capabilities.Should().BeNull("an inactive link must read the same as no link at all");
        scope.Dispose();
    }

    [Fact]
    public async Task GetCapabilitiesByClientPublicIdAsync_ActiveLinkNeitherCapability_ReturnsNonNullGrantsNothing()
    {
        var professionalUserId = await RegisterProfessionalAsync("pub-grants-nothing");
        var (clientPublicId, clientUserId) = await RegisterClientAsync("pub-grants-nothing");
        await CreateLinkAsync(professionalUserId, clientUserId, isActive: true, canViewNutritionPlans: false, canViewTrainingPlans: false);
        var service = CreateService(out var scope);

        var capabilities = await service.GetCapabilitiesByClientPublicIdAsync(
            professionalUserId, clientPublicId, TestContext.Current.CancellationToken);

        capabilities.Should().NotBeNull("an active link that grants nothing is still a link — 403, not 404");
        capabilities!.Value.GrantsNothing.Should().BeTrue();
        scope.Dispose();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task GetCapabilitiesByClientPublicIdAsync_ActiveLink_ReturnsMatchingFlags(
        bool canViewTrainingPlans, bool canViewNutritionPlans)
    {
        var tag = $"pub-flags-{canViewTrainingPlans}-{canViewNutritionPlans}";
        var professionalUserId = await RegisterProfessionalAsync(tag);
        var (clientPublicId, clientUserId) = await RegisterClientAsync(tag);
        await CreateLinkAsync(professionalUserId, clientUserId, isActive: true, canViewNutritionPlans, canViewTrainingPlans);
        var service = CreateService(out var scope);

        var capabilities = await service.GetCapabilitiesByClientPublicIdAsync(
            professionalUserId, clientPublicId, TestContext.Current.CancellationToken);

        capabilities.Should().NotBeNull();
        capabilities!.Value.CanViewTrainingPlans.Should().Be(canViewTrainingPlans);
        capabilities.Value.CanViewNutritionPlans.Should().Be(canViewNutritionPlans);
        scope.Dispose();
    }

    // ── GetCapabilitiesByClientUserIdAsync — plan-addressed variant ─────────────

    [Fact]
    public async Task GetCapabilitiesByClientUserIdAsync_NoProfessionalProfile_ReturnsNull()
    {
        var (_, clientUserId) = await RegisterClientAsync("user-no-prof");
        var service = CreateService(out var scope);

        var capabilities = await service.GetCapabilitiesByClientUserIdAsync(
            Guid.NewGuid(), clientUserId, TestContext.Current.CancellationToken);

        capabilities.Should().BeNull();
        scope.Dispose();
    }

    [Fact]
    public async Task GetCapabilitiesByClientUserIdAsync_NoClientProfile_ReturnsNull()
    {
        var professionalUserId = await RegisterProfessionalAsync("user-no-client");
        var service = CreateService(out var scope);

        var capabilities = await service.GetCapabilitiesByClientUserIdAsync(
            professionalUserId, Guid.NewGuid(), TestContext.Current.CancellationToken);

        capabilities.Should().BeNull();
        scope.Dispose();
    }

    [Fact]
    public async Task GetCapabilitiesByClientUserIdAsync_ActiveLink_MatchesClientPublicIdVariant()
    {
        var professionalUserId = await RegisterProfessionalAsync("addressing-equiv");
        var (clientPublicId, clientUserId) = await RegisterClientAsync("addressing-equiv");
        await CreateLinkAsync(professionalUserId, clientUserId, isActive: true, canViewNutritionPlans: true, canViewTrainingPlans: false);
        var service = CreateService(out var scope);
        var ct = TestContext.Current.CancellationToken;

        var byPublicId = await service.GetCapabilitiesByClientPublicIdAsync(professionalUserId, clientPublicId, ct);
        var byUserId = await service.GetCapabilitiesByClientUserIdAsync(professionalUserId, clientUserId, ct);

        byPublicId.Should().NotBeNull();
        byUserId.Should().NotBeNull();
        byUserId!.Value.Should().Be(byPublicId!.Value, "the same link resolved either way must yield identical capabilities");
        scope.Dispose();
    }

    // ── GetAccessibleClientsAsync — batch variant ───────────────────────────────

    [Fact]
    public async Task GetAccessibleClientsAsync_NoProfessionalProfile_ReturnsEmptyList()
    {
        var service = CreateService(out var scope);

        var accessible = await service.GetAccessibleClientsAsync(
            Guid.NewGuid(), TestContext.Current.CancellationToken);

        accessible.Should().BeEmpty();
        scope.Dispose();
    }

    [Fact]
    public async Task GetAccessibleClientsAsync_NoDomainFilter_IncludesLinkThatGrantsNothing()
    {
        var professionalUserId = await RegisterProfessionalAsync("batch-grants-nothing");
        var (_, clientUserId) = await RegisterClientAsync("batch-grants-nothing");
        await CreateLinkAsync(professionalUserId, clientUserId, isActive: true, canViewNutritionPlans: false, canViewTrainingPlans: false);
        var service = CreateService(out var scope);

        var accessible = await service.GetAccessibleClientsAsync(
            professionalUserId, TestContext.Current.CancellationToken);

        accessible.Should().ContainSingle(entry => entry.ClientUserId == clientUserId);
        accessible.Single(entry => entry.ClientUserId == clientUserId).Capabilities.GrantsNothing.Should().BeTrue(
            "the unfiltered batch variant gates on IsActive only — adding a capability predicate " +
            "would silently drop GrantsNothing rows, which is exactly the distinction this AC protects");
        scope.Dispose();
    }

    [Fact]
    public async Task GetAccessibleClientsAsync_InactiveLink_IsExcluded()
    {
        var professionalUserId = await RegisterProfessionalAsync("batch-inactive");
        var (_, clientUserId) = await RegisterClientAsync("batch-inactive");
        await CreateLinkAsync(professionalUserId, clientUserId, isActive: false, canViewNutritionPlans: true, canViewTrainingPlans: true);
        var service = CreateService(out var scope);

        var accessible = await service.GetAccessibleClientsAsync(
            professionalUserId, TestContext.Current.CancellationToken);

        accessible.Should().BeEmpty();
        scope.Dispose();
    }

    [Fact]
    public async Task GetAccessibleClientsAsync_WithTrainingFilter_ExcludesLinksWithoutTrainingCapability()
    {
        var professionalUserId = await RegisterProfessionalAsync("batch-training-filter");
        var (_, trainingClientUserId) = await RegisterClientAsync("batch-training-filter-yes");
        var (_, nutritionOnlyClientUserId) = await RegisterClientAsync("batch-training-filter-no");
        await CreateLinkAsync(professionalUserId, trainingClientUserId, isActive: true, canViewNutritionPlans: false, canViewTrainingPlans: true);
        await CreateLinkAsync(professionalUserId, nutritionOnlyClientUserId, isActive: true, canViewNutritionPlans: true, canViewTrainingPlans: false);
        var service = CreateService(out var scope);

        var accessible = await service.GetAccessibleClientsAsync(
            professionalUserId, TestContext.Current.CancellationToken, requiredScope: LinkCapabilityScope.TrainingOnly);

        accessible.Should().ContainSingle();
        accessible.Single().ClientUserId.Should().Be(trainingClientUserId);
        scope.Dispose();
    }

    [Fact]
    public async Task GetAccessibleClientsAsync_WithNutritionFilter_ExcludesLinksWithoutNutritionCapability()
    {
        var professionalUserId = await RegisterProfessionalAsync("batch-nutrition-filter");
        var (_, trainingOnlyClientUserId) = await RegisterClientAsync("batch-nutrition-filter-no");
        var (_, nutritionClientUserId) = await RegisterClientAsync("batch-nutrition-filter-yes");
        await CreateLinkAsync(professionalUserId, trainingOnlyClientUserId, isActive: true, canViewNutritionPlans: false, canViewTrainingPlans: true);
        await CreateLinkAsync(professionalUserId, nutritionClientUserId, isActive: true, canViewNutritionPlans: true, canViewTrainingPlans: false);
        var service = CreateService(out var scope);

        var accessible = await service.GetAccessibleClientsAsync(
            professionalUserId, TestContext.Current.CancellationToken, requiredScope: LinkCapabilityScope.NutritionOnly);

        accessible.Should().ContainSingle();
        accessible.Single().ClientUserId.Should().Be(nutritionClientUserId);
        scope.Dispose();
    }

    [Fact]
    public async Task GetAccessibleClientsAsync_WithBothScope_RequiresBothCapabilityFlags()
    {
        var professionalUserId = await RegisterProfessionalAsync("batch-both-filter");
        var (_, bothClientUserId) = await RegisterClientAsync("batch-both-filter-yes");
        var (_, trainingOnlyClientUserId) = await RegisterClientAsync("batch-both-filter-training-only");
        var (_, nutritionOnlyClientUserId) = await RegisterClientAsync("batch-both-filter-nutrition-only");
        await CreateLinkAsync(professionalUserId, bothClientUserId, isActive: true, canViewNutritionPlans: true, canViewTrainingPlans: true);
        await CreateLinkAsync(professionalUserId, trainingOnlyClientUserId, isActive: true, canViewNutritionPlans: false, canViewTrainingPlans: true);
        await CreateLinkAsync(professionalUserId, nutritionOnlyClientUserId, isActive: true, canViewNutritionPlans: true, canViewTrainingPlans: false);
        var service = CreateService(out var scope);

        var accessible = await service.GetAccessibleClientsAsync(
            professionalUserId, TestContext.Current.CancellationToken, requiredScope: LinkCapabilityScope.Both);

        accessible.Should().ContainSingle(
            "Both must require CanViewTrainingPlans AND CanViewNutritionPlans, excluding either single-domain link");
        accessible.Single().ClientUserId.Should().Be(bothClientUserId);
        scope.Dispose();
    }
}
