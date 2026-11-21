using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.Users;

/// <summary>
/// Integration tests for <c>GET /users/me/roles</c>.
/// </summary>
[Collection(TestCollection.Shard4)]
public class GetMyCoachRolesIntegrationTests(FitnessApiFactory factory)
{
    private record RoleSummary(string Role, int ClientCount, int SharedWithOtherRoleCount);

    private record RolesResult(List<RoleSummary> Roles);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<List<RoleSummary>> GetRolesAsync(HttpClient http)
    {
        var response = await http.GetAsync("/users/me/roles", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<RolesResult>(cancellationToken: Ct);
        return body!.Roles;
    }

    [Fact]
    public async Task GetRoles_DualRoleCoach_CountsClientsAndSharedPerRole()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var trainingOnly = await TestActors.Client(factory).CreateAsync(Ct);
        var nutritionOnly = await TestActors.Client(factory).CreateAsync(Ct);
        var both = await TestActors.Client(factory).CreateAsync(Ct);
        var inactive = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, trainingOnly).CanViewNutritionPlans(false).CreateAsync(Ct);
        await TestActors.Link(factory, coach, nutritionOnly)
            .AsRole(UserRole.Nutritionist).CanViewTrainingPlans(false).CreateAsync(Ct);
        await TestActors.Link(factory, coach, both).CreateAsync(Ct);
        await TestActors.Link(factory, coach, inactive).Inactive().CreateAsync(Ct);

        var roles = await GetRolesAsync(coach.Http);

        roles.Should().BeEquivalentTo(
        [
            new RoleSummary(AppRoles.Trainer, 2, 1),
            new RoleSummary(AppRoles.Nutritionist, 2, 1),
        ]);
    }

    [Fact]
    public async Task GetRoles_AfterRemovingTrainer_ListsOnlyNutritionistWithNothingShared()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var both = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, both).CreateAsync(Ct);
        (await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var roles = await GetRolesAsync(coach.Http);

        roles.Should().Equal(new RoleSummary(AppRoles.Nutritionist, 1, 0));
    }

    [Fact]
    public async Task GetRoles_SingleRoleCoachWithDualFlagLink_CountsClientButNotShared()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);

        var roles = await GetRolesAsync(coach.Http);

        roles.Should().Equal(new RoleSummary(AppRoles.Trainer, 1, 0));
    }
}
