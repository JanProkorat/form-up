using System.Net;
using System.Net.Http.Json;
using System.Text;
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

namespace FormUp.Tests.Endpoints.Client.Invites;

/// <summary>
/// Accepting an invite from a coach the client is already linked to widens the active link to the invite's
/// scope, never narrows it, and still honours the removed-role and one-coach-per-profession guards.
/// </summary>
[Collection(TestCollection.Shard3)]
public class AcceptInviteWidensActiveLinkIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpResponseMessage Response, Guid InvitePublicId)> AcceptAsync(
        bool byToken, Actor coach, Actor client, LinkCapabilityScope scope)
    {
        using var serviceScope = factory.Services.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var invite = EntityBuilder.PendingInvite
            .WithProfessionalProfileId(coach.ProfileId)
            .WithEmail(client.Email)
            .WithRequestedScope(scope)
            .Build();
        invite.PublicId = Guid.NewGuid();
        db.PendingInvites.Add(invite);

        var token = $"token-{Guid.NewGuid():N}";
        db.InvitationTokens.Add(EntityBuilder.InvitationToken
            .WithProfessionalProfileId(coach.ProfileId)
            .WithEmail(client.Email)
            .WithToken(token)
            .WithRequestedScope(scope)
            .Build());
        await db.SaveChangesAsync(Ct);

        var response = byToken
            ? await client.Http.PostAsJsonAsync("/auth/invite/accept", new { Token = token }, Ct)
            : await client.Http.PostAsync(
                $"/client/invites/{invite.PublicId}/accept", new StringContent("{}", Encoding.UTF8, "application/json"), Ct);

        return (response, invite.PublicId);
    }

    private async Task<ClientProfessionalLink> LinkOfAsync(Actor coach, Actor client)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientProfessionalLinks.AsNoTracking()
            .SingleAsync(l => l.ClientProfileId == client.ProfileId && l.ProfessionalProfileId == coach.ProfileId, Ct);
    }

    private async Task<PendingInviteStatus> InviteStatusAsync(Guid invitePublicId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.PendingInvites.AsNoTracking()
            .Where(i => i.PublicId == invitePublicId).Select(i => i.Status).SingleAsync(Ct);
    }

    private static async Task AssertCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var raw = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(status, raw);
        using var document = JsonDocument.Parse(raw);
        document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString().Should().Be(code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accept_TrainingLinkThenNutritionInvite_WidensTheLinkToBoth(bool byToken)
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CanViewTrainingPlans().CanViewNutritionPlans(false).CreateAsync(Ct);
        var before = await LinkOfAsync(coach, client);

        var (response, invitePublicId) = await AcceptAsync(byToken, coach, client, LinkCapabilityScope.NutritionOnly);

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
        var link = await LinkOfAsync(coach, client);
        link.CanViewNutritionPlans.Should().BeTrue();
        link.CanViewTrainingPlans.Should().BeTrue();
        link.ProfessionalRole.Should().Be(before.ProfessionalRole);
        link.QuestionnaireId.Should().Be(before.QuestionnaireId);
        link.ActivatedAtUtc.Should().Be(before.ActivatedAtUtc);
        (await InviteStatusAsync(invitePublicId)).Should().Be(PendingInviteStatus.Accepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accept_WhenAnotherCoachHoldsTheNewSlot_Returns400AndLeavesTheLinkUnchanged(bool byToken)
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var otherCoach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CanViewTrainingPlans().CanViewNutritionPlans(false).CreateAsync(Ct);
        await TestActors.Link(factory, otherCoach, client).CanViewNutritionPlans().CanViewTrainingPlans(false).CreateAsync(Ct);

        var (response, invitePublicId) = await AcceptAsync(byToken, coach, client, LinkCapabilityScope.NutritionOnly);

        await AssertCodeAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ProfessionAlreadyOccupied);
        var link = await LinkOfAsync(coach, client);
        link.CanViewNutritionPlans.Should().BeFalse();
        link.CanViewTrainingPlans.Should().BeTrue();
        (await InviteStatusAsync(invitePublicId)).Should().Be(PendingInviteStatus.Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accept_WhenTheNewRoleWasRemoved_Returns403AndLeavesTheLinkUnchanged(bool byToken)
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CanViewTrainingPlans().CanViewNutritionPlans(false).CreateAsync(Ct);
        (await coach.Http.DeleteAsync("/users/me/roles/Nutritionist", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var (response, invitePublicId) = await AcceptAsync(byToken, coach, client, LinkCapabilityScope.NutritionOnly);

        await AssertCodeAsync(response, HttpStatusCode.Forbidden, ErrorCodes.CoachRoleRemoved);
        var link = await LinkOfAsync(coach, client);
        link.CanViewNutritionPlans.Should().BeFalse();
        link.CanViewTrainingPlans.Should().BeTrue();
        (await InviteStatusAsync(invitePublicId)).Should().Be(PendingInviteStatus.Pending);
    }

    [Fact]
    public async Task AcceptInApp_WhenTheInviteAddsNothing_ChangesNoFlagAndAcceptsTheInvite()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CanViewTrainingPlans().CanViewNutritionPlans(false).CreateAsync(Ct);

        var (response, invitePublicId) = await AcceptAsync(false, coach, client, LinkCapabilityScope.TrainingOnly);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
        var link = await LinkOfAsync(coach, client);
        link.CanViewNutritionPlans.Should().BeFalse();
        link.CanViewTrainingPlans.Should().BeTrue();
        (await InviteStatusAsync(invitePublicId)).Should().Be(PendingInviteStatus.Accepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accept_WhenTheLinkAlreadyCoversBoth_NeverNarrowsIt(bool byToken)
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CanViewTrainingPlans().CanViewNutritionPlans().CreateAsync(Ct);

        var (response, _) = await AcceptAsync(byToken, coach, client, LinkCapabilityScope.TrainingOnly);

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
        var link = await LinkOfAsync(coach, client);
        link.CanViewNutritionPlans.Should().BeTrue();
        link.CanViewTrainingPlans.Should().BeTrue();
    }
}
