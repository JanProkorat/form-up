using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Professionals;

/// <summary>
/// A removed coach role is hidden from marketplace search and the public profile.
/// </summary>
[Collection(TestCollection.Shard3)]
public class CoachRoleVisibilityTests(FitnessApiFactory factory)
{
    private record SearchItem(Guid PublicId, List<string> Roles);

    private record SearchResult(List<SearchItem> Items);

    private record ProfileResult(List<string> Roles);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Actor Coach, Actor Client, string LastName)> DualCoachAndClientAsync()
    {
        var lastName = $"Vis{Guid.NewGuid():N}";
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist)
            .WithName("Visible", lastName).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        return (coach, client, lastName);
    }

    private async Task<List<SearchItem>> SearchAsync(Actor client, string lastName, string? role = null)
    {
        var url = $"/professionals/search?search={lastName}" + (role is null ? string.Empty : $"&role={role}");
        var response = await client.Http.GetAsync(url, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SearchResult>(cancellationToken: Ct))!.Items;
    }

    [Fact]
    public async Task Search_RemovedRole_IsDroppedFromRolesAndRoleFilter()
    {
        var (coach, client, lastName) = await DualCoachAndClientAsync();
        (await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var all = await SearchAsync(client, lastName);
        var trainers = await SearchAsync(client, lastName, AppRoles.Trainer);
        var nutritionists = await SearchAsync(client, lastName, AppRoles.Nutritionist);

        all.Should().ContainSingle().Which.Roles.Should().Equal(AppRoles.Nutritionist);
        trainers.Should().BeEmpty();
        nutritionists.Should().ContainSingle();
    }

    [Fact]
    public async Task Search_CoachWithNoActiveRole_Disappears()
    {
        var (coach, client, lastName) = await DualCoachAndClientAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var profile = await db.ProfessionalProfiles.FirstAsync(p => p.UserId == coach.UserId, Ct);
            profile.TrainerRoleRemovedAt = DateTime.UtcNow;
            profile.NutritionistRoleRemovedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        (await SearchAsync(client, lastName)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetPublicProfile_RemovedRole_IsNotListed()
    {
        var (coach, client, _) = await DualCoachAndClientAsync();
        (await coach.Http.DeleteAsync("/users/me/roles/Nutritionist", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.Http.GetAsync($"/professionals/{coach.PublicId}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ProfileResult>(cancellationToken: Ct);
        body!.Roles.Should().Equal(AppRoles.Trainer);
    }
}
