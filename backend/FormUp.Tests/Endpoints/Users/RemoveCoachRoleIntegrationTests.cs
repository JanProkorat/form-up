using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Users;

/// <summary>
/// Integration tests for <c>DELETE /users/me/roles/{Role}</c>.
/// </summary>
[Collection(TestCollection.Name)]
public class RemoveCoachRoleIntegrationTests(FitnessApiFactory factory)
{
    private record RoleSummary(string Role, int ClientCount, int SharedWithOtherRoleCount);

    private record RemoveResult(string RemovedRole, List<RoleSummary> Roles);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Remove_DualRoleTrainer_MarksRemoved_KeepsIdentityRole_AndLeavesLinkFlags()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var linkId = await TestActors.Link(factory, coach, client).CreateAsync(Ct);

        var response = await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.ProfessionalProfiles.AsNoTracking().FirstAsync(p => p.UserId == coach.UserId, Ct);
        profile.TrainerRoleRemovedAt.Should().NotBeNull();
        profile.NutritionistRoleRemovedAt.Should().BeNull();

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(coach.UserId.ToString());
        (await userManager.GetRolesAsync(user!)).Should().Contain(AppRoles.Trainer);

        var link = await db.ClientProfessionalLinks.AsNoTracking().FirstAsync(l => l.Id == linkId, Ct);
        link.ConnectionState.Should().Be(ConnectionState.Connected);
        link.CanViewTrainingPlans.Should().BeTrue();
        link.CanViewNutritionPlans.Should().BeTrue();
    }

    [Fact]
    public async Task Remove_ReturnsCountsAsTheyStoodBeforeRemoval()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var trainingOnly = await TestActors.Client(factory).CreateAsync(Ct);
        var both = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, trainingOnly).CanViewNutritionPlans(false).CreateAsync(Ct);
        await TestActors.Link(factory, coach, both).CreateAsync(Ct);

        var response = await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct);

        var body = await response.Content.ReadFromJsonAsync<RemoveResult>(cancellationToken: Ct);
        body!.RemovedRole.Should().Be(AppRoles.Trainer);
        body.Roles.Single(r => r.Role == AppRoles.Trainer).Should().Be(new RoleSummary(AppRoles.Trainer, 2, 1));
        body.Roles.Single(r => r.Role == AppRoles.Nutritionist).Should().Be(new RoleSummary(AppRoles.Nutritionist, 1, 1));
    }

    [Fact]
    public async Task Remove_NotifiesOnlyClientsWhoseActiveLinkGrantsTheRemovedDiscipline()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var trainingClient = await TestActors.Client(factory).CreateAsync(Ct);
        var nutritionOnlyClient = await TestActors.Client(factory).CreateAsync(Ct);
        var endedLinkClient = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, trainingClient).CreateAsync(Ct);
        await TestActors.Link(factory, coach, nutritionOnlyClient).CanViewTrainingPlans(false).CreateAsync(Ct);
        await TestActors.Link(factory, coach, endedLinkClient).Inactive().CreateAsync(Ct);

        var response = await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountRoleRemovedNotificationsAsync(trainingClient.UserId)).Should().Be(1);
        (await CountRoleRemovedNotificationsAsync(nutritionOnlyClient.UserId)).Should().Be(0);
        (await CountRoleRemovedNotificationsAsync(endedLinkClient.UserId)).Should().Be(0);
    }

    private async Task<int> CountRoleRemovedNotificationsAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Notifications.AsNoTracking()
            .CountAsync(n => n.RecipientUserId == userId && n.Type == NotificationType.CoachRoleRemoved, Ct);
    }

    [Fact]
    public async Task Remove_OnlyActiveCoachRole_Returns400OnlyCoachRole()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        var response = await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.OnlyCoachRole);
    }

    [Fact]
    public async Task Remove_OnlyCoachRoleWhileAlsoClient_StillReturns400OnlyCoachRole()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Client).CreateAsync(Ct);

        var response = await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.OnlyCoachRole);
    }

    [Fact]
    public async Task Remove_RoleNotHeld_Returns400RoleNotAssigned()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        var response = await coach.Http.DeleteAsync("/users/me/roles/Nutritionist", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.RoleNotAssigned);
    }

    [Fact]
    public async Task Remove_AlreadyRemovedRole_Returns400RoleNotAssigned()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        (await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.RoleNotAssigned);
    }

    [Fact]
    public async Task Remove_NonCoachRole_Returns400()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);

        var response = await coach.Http.DeleteAsync("/users/me/roles/Admin", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
