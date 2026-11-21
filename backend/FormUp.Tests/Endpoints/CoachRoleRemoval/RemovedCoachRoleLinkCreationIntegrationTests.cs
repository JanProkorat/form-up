using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.CoachRoleRemoval;

/// <summary>
/// A removed coach role may not seed a new client link: invite creation and all three accept paths
/// refuse a scope covering only the removed discipline, and a missing scope grants active disciplines only.
/// </summary>
[Collection(TestCollection.Shard2)]
public class RemovedCoachRoleLinkCreationIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Actor> CoachWithTrainerRemovedAsync()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        (await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        return coach;
    }

    private static async Task AssertCoachRoleRemovedAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, raw);
        using var document = JsonDocument.Parse(raw);
        document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString()
            .Should().Be(ErrorCodes.CoachRoleRemoved);
    }

    [Fact]
    public async Task CreatePendingInvite_ScopeCoveringOnlyTheRemovedRole_Returns403()
    {
        var coach = await CoachWithTrainerRemovedAsync();

        var response = await coach.Http.PostAsJsonAsync(
            "/trainer/pending-invites",
            new { Email = $"{Guid.NewGuid():N}@invite-fixture.com", RequestedScope = "TrainingOnly" },
            Ct);

        await AssertCoachRoleRemovedAsync(response);
    }

    [Fact]
    public async Task AcceptClientRequest_ScopeCoveringOnlyTheRemovedRole_Returns403()
    {
        var coach = await CoachWithTrainerRemovedAsync();
        var requestId = await SeedClientRequestAsync(coach);

        var response = await coach.Http.PostAsJsonAsync(
            $"/trainer/client-requests/{requestId}/accept", new { RequestedScope = "TrainingOnly" }, Ct);

        await AssertCoachRoleRemovedAsync(response);
    }

    [Fact]
    public async Task AcceptClientRequest_WithoutScope_GrantsOnlyTheActiveDisciplineFlag()
    {
        var coach = await CoachWithTrainerRemovedAsync();
        var requestId = await SeedClientRequestAsync(coach);

        var response = await coach.Http.PostAsJsonAsync($"/trainer/client-requests/{requestId}/accept", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var link = await db.ClientProfessionalLinks.AsNoTracking()
            .SingleAsync(l => l.ProfessionalProfileId == coach.ProfileId, Ct);
        link.CanViewNutritionPlans.Should().BeTrue();
        link.CanViewTrainingPlans.Should().BeFalse();
        link.ProfessionalRole.Should().Be(UserRole.Nutritionist);
    }

    [Fact]
    public async Task AcceptClientInvite_StoredScopeCoveringOnlyTheRemovedRole_Returns403()
    {
        var coach = await CoachWithTrainerRemovedAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var invitePublicId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var invite = EntityBuilder.PendingInvite
                .WithProfessionalProfileId(coach.ProfileId)
                .WithEmail(client.Email)
                .WithRequestedScope(LinkCapabilityScope.TrainingOnly)
                .Build();
            invite.PublicId = invitePublicId;
            db.PendingInvites.Add(invite);
            await db.SaveChangesAsync(Ct);
        }

        var response = await client.Http.PostAsJsonAsync($"/client/invites/{invitePublicId}/accept", new { }, Ct);

        await AssertCoachRoleRemovedAsync(response);
    }

    [Fact]
    public async Task AcceptInvitation_StoredScopeCoveringOnlyTheRemovedRole_Returns403()
    {
        var coach = await CoachWithTrainerRemovedAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var token = $"token-{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.InvitationTokens.Add(EntityBuilder.InvitationToken
                .WithProfessionalProfileId(coach.ProfileId)
                .WithEmail(client.Email)
                .WithToken(token)
                .WithRequestedScope(LinkCapabilityScope.TrainingOnly)
                .Build());
            await db.SaveChangesAsync(Ct);
        }

        var response = await client.Http.PostAsJsonAsync("/auth/invite/accept", new { Token = token }, Ct);

        await AssertCoachRoleRemovedAsync(response);
    }

    private async Task<Guid> SeedClientRequestAsync(Actor coach)
    {
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var request = new ClientRequest { ClientProfileId = client.ProfileId, ProfessionalProfileId = coach.ProfileId };
        db.ClientRequests.Add(request);
        await db.SaveChangesAsync(Ct);
        return request.PublicId;
    }
}
