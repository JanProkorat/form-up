using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Authorization;

/// <summary>
/// Regression tests for #903 — the cross-role capability-escalation hole. Before the fix, the
/// nutrition and training link-presence checks gated on
/// <see cref="ClientProfessionalLink.IsActive"/> only. Because <c>POST /users/me/roles</c>
/// self-assignment never touches existing links, a professional already linked to a client under
/// one role could self-assign the other role and immediately pass the other domain's link check
/// for that same client, despite the link never granting that capability.
/// <para>
/// Reproduction shape (per the issue): register a professional, self-assign one role, create an
/// active link under that role (so the link's capability flags are stamped for that role only),
/// then self-assign the other role and confirm the other domain's endpoints still deny access
/// because the pre-existing link's capability flag stayed <see langword="false"/>.
/// </para>
/// </summary>
[Collection(TestCollection.Shard2)]
public class CrossRoleLinkAccessTests(FitnessApiFactory factory)
{
    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@cross-role-{tag}.com";

    /// <summary>
    /// Registers a professional under <paramref name="initialRole"/> and authenticates the
    /// returned client with that role's access token.
    /// </summary>
    /// <remarks>
    /// #1104: built directly via <see cref="TestActors"/> — the register/login HTTP round trip
    /// is not the thing under test here; only <see cref="SelfAssignRoleAsync"/> (below) is, and
    /// it still goes through the real <c>/users/me/roles</c> endpoint.
    /// </remarks>
    private async Task<(HttpClient Client, Guid UserId)> RegisterProfessionalAsync(string tag, string initialRole)
    {
        var actor = await TestActors.Professional(factory, Enum.Parse<UserRole>(initialRole, ignoreCase: true))
            .WithEmail(UniqueEmail(tag))
            .CreateAsync(TestContext.Current.CancellationToken);

        return (actor.Http, actor.UserId);
    }

    private async Task<(Guid ClientPublicId, long ClientProfileId)> RegisterClientAsync(string tag)
    {
        var actor = await TestActors.Client(factory)
            .WithEmail(UniqueEmail(tag))
            .CreateAsync(TestContext.Current.CancellationToken);

        return (actor.PublicId, actor.ProfileId);
    }

    private async Task<long> GetProfessionalProfileIdAsync(Guid professionalUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.ProfessionalProfiles.FirstAsync(
            p => p.UserId == professionalUserId, TestContext.Current.CancellationToken);
        return profile.Id;
    }

    /// <summary>
    /// Inserts an active <see cref="ClientProfessionalLink"/> directly, with capability flags
    /// stamped exactly as the real write paths stamp them from role claims at link-creation time
    /// (never re-stamped afterward — that stale-false is exactly what #903 closes).
    /// </summary>
    private async Task LinkAsync(
        long professionalProfileId, long clientProfileId, UserRole role, bool canViewTrainingPlans, bool canViewNutritionPlans) =>
        await TestActors.Link(factory, professionalProfileId, clientProfileId)
            .AsRole(role)
            .CanViewTrainingPlans(canViewTrainingPlans)
            .CanViewNutritionPlans(canViewNutritionPlans)
            .CreateAsync(TestContext.Current.CancellationToken);

    /// <summary>
    /// Self-assigns <paramref name="role"/> via <c>POST /users/me/roles</c> and re-authenticates
    /// <paramref name="client"/> with the fresh access token the endpoint returns.
    /// <para>
    /// Trap this guards against: the pre-existing JWT lacks the new role claim, so reusing it
    /// would 403 at the target endpoint's <c>Roles()</c> attribute before ever reaching the auth
    /// helper under test — a false pass for the wrong reason.
    /// </para>
    /// </summary>
    private static async Task SelfAssignRoleAsync(HttpClient client, string role)
    {
        var response = await client.PostAsJsonAsync("/users/me/roles", new { Role = role });
        response.StatusCode.Should().Be(HttpStatusCode.OK, "self-assigning an already-registrable role must succeed");

        var body = await response.Content.ReadFromJsonAsync<AddRoleResponseDto>(
            cancellationToken: TestContext.Current.CancellationToken);
        TestHelpers.SetBearerToken(client, body!.AccessToken);
    }

    private static NutritionPlanTemplate BuildMinimalTemplate(Guid ownerId) => new()
    {
        ExternalId = Guid.NewGuid(),
        OwnerId = ownerId,
        Name = "Cross-Role Test Template",
        Visibility = LibraryVisibility.Private,
        Version = 1,
        DateCreated = DateTime.UtcNow,
        Weeks =
        [
            new TemplateWeek
            {
                WeekNumber = 1,
                Days = Enumerable.Range(1, 7).Select(dayOfWeek => new PlanDay
                {
                    DayOfWeek = dayOfWeek,
                    Meals = []
                }).ToList()
            }
        ],
        WeekCount = 1
    };

    private async Task SeedTemplateAsync(NutritionPlanTemplate template)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.NutritionPlanTemplates.InsertOneAsync(
            template, cancellationToken: TestContext.Current.CancellationToken);
    }

    // ── nutrition-scoped write paths deny a stale trainer-only link ──────────

    [Fact]
    public async Task TrainerOnlyLink_SelfAssignsNutritionist_CreateNutritionPlan_Returns404()
    {
        var (professional, professionalId) = await RegisterProfessionalAsync("trainer-to-nutrition-plan", "Trainer");
        var (clientPublicId, clientProfileId) = await RegisterClientAsync("trainer-to-nutrition-plan");
        var professionalProfileId = await GetProfessionalProfileIdAsync(professionalId);

        // Link created while Trainer-only: CanViewNutritionPlans stamped false, never re-stamped.
        await LinkAsync(professionalProfileId, clientProfileId, UserRole.Trainer, canViewTrainingPlans: true, canViewNutritionPlans: false);

        await SelfAssignRoleAsync(professional, "Nutritionist");

        var response = await professional.PostAsJsonAsync("/nutrition/plans", new
        {
            ClientId = clientPublicId,
            Name = "Escalation Attempt"
        });

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "the pre-existing link never granted CanViewNutritionPlans, so self-assigning Nutritionist must not unlock nutrition writes for this client");
    }

    [Fact]
    public async Task TrainerOnlyLink_SelfAssignsNutritionist_InstantiateNutritionTemplate_Returns404()
    {
        var (professional, professionalId) = await RegisterProfessionalAsync("trainer-to-instantiate", "Trainer");
        var (clientPublicId, clientProfileId) = await RegisterClientAsync("trainer-to-instantiate");
        var professionalProfileId = await GetProfessionalProfileIdAsync(professionalId);

        await LinkAsync(professionalProfileId, clientProfileId, UserRole.Trainer, canViewTrainingPlans: true, canViewNutritionPlans: false);

        await SelfAssignRoleAsync(professional, "Nutritionist");

        var template = BuildMinimalTemplate(professionalId);
        await SeedTemplateAsync(template);

        var response = await professional.PostAsJsonAsync(
            $"/nutrition/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientPublicId, Name = "Escalation Attempt" });

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "instantiate replicates the same coach-client link check as CreatePlan and must deny the stale-false link the same way");
    }

    // ── training-scoped write path denies a stale nutritionist-only link ─────

    [Fact]
    public async Task NutritionistOnlyLink_SelfAssignsTrainer_CreateTrainingPlan_Returns404()
    {
        var (professional, professionalId) = await RegisterProfessionalAsync("nutrition-to-training-plan", "Nutritionist");
        var (clientPublicId, clientProfileId) = await RegisterClientAsync("nutrition-to-training-plan");
        var professionalProfileId = await GetProfessionalProfileIdAsync(professionalId);

        // Link created while Nutritionist-only: CanViewTrainingPlans stamped false, never re-stamped.
        await LinkAsync(professionalProfileId, clientProfileId, UserRole.Nutritionist, canViewTrainingPlans: false, canViewNutritionPlans: true);

        await SelfAssignRoleAsync(professional, "Trainer");

        var response = await professional.PostAsJsonAsync("/training/plans", new
        {
            ClientId = clientPublicId,
            Name = "Escalation Attempt"
        });

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "the pre-existing link never granted CanViewTrainingPlans, so self-assigning Trainer must not unlock training writes for this client");
    }

    // ── GetClientProgress stays dual-readable, but still requires a granted capability ──

    [Fact]
    public async Task NutritionistOnlyLink_GetClientProgress_Returns200()
    {
        var (professional, professionalId) = await RegisterProfessionalAsync("nutritionist-progress", "Nutritionist");
        var (clientPublicId, clientProfileId) = await RegisterClientAsync("nutritionist-progress");
        var professionalProfileId = await GetProfessionalProfileIdAsync(professionalId);

        await LinkAsync(professionalProfileId, clientProfileId, UserRole.Nutritionist, canViewTrainingPlans: false, canViewNutritionPlans: true);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/progress");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "GetClientProgress is deliberately dual-readable — a nutritionist-only link must still reach it via HasAnyPlanAccessAsync");
    }

    [Fact]
    public async Task TrainerOnlyLink_GetClientProgress_Returns200()
    {
        var (professional, professionalId) = await RegisterProfessionalAsync("trainer-progress", "Trainer");
        var (clientPublicId, clientProfileId) = await RegisterClientAsync("trainer-progress");
        var professionalProfileId = await GetProfessionalProfileIdAsync(professionalId);

        await LinkAsync(professionalProfileId, clientProfileId, UserRole.Trainer, canViewTrainingPlans: true, canViewNutritionPlans: false);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/progress");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "GetClientProgress is deliberately dual-readable — a trainer-only link must still reach it via HasAnyPlanAccessAsync");
    }

    [Fact]
    public async Task ActiveLinkWithNeitherCapability_GetClientProgress_Returns404()
    {
        var (professional, professionalId) = await RegisterProfessionalAsync("neither-capability-progress", "Trainer");
        var (clientPublicId, clientProfileId) = await RegisterClientAsync("neither-capability-progress");
        var professionalProfileId = await GetProfessionalProfileIdAsync(professionalId);

        // Active link, but neither capability flag granted.
        await LinkAsync(professionalProfileId, clientProfileId, UserRole.Trainer, canViewTrainingPlans: false, canViewNutritionPlans: false);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/progress");

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "HasAnyPlanAccessAsync must still require at least one granted capability — an IsActive-only link is not enough");
    }

    private sealed class AddRoleResponseDto
    {
        public string AccessToken { get; set; } = string.Empty;
    }
}
