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
using static FormUp.Tests.Endpoints.ClientPayments.ClientPaymentFixtures;

namespace FormUp.Tests.Endpoints.Client.Invites;

/// <summary>
/// A former client whose link ended and who is invited again gets the link switched back on, through both
/// the in-app accept and the emailed-token accept.
/// </summary>
[Collection(TestCollection.Name)]
public class ReinviteReactivatesLinkIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpResponseMessage> AcceptAsync(
        bool byToken, Actor coach, Actor client, LinkCapabilityScope? scope = null, long? questionnaireId = null)
    {
        using (var scopeFactory = factory.Services.CreateScope())
        {
            var db = scopeFactory.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var invite = EntityBuilder.PendingInvite
                .WithProfessionalProfileId(coach.ProfileId)
                .WithEmail(client.Email)
                .WithRequestedScope(scope)
                .Build();
            invite.QuestionnaireId = questionnaireId;
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

            return byToken
                ? await client.Http.PostAsJsonAsync("/auth/invite/accept", new { Token = token }, Ct)
                : await client.Http.PostAsync(
                    $"/client/invites/{invite.PublicId}/accept", new StringContent("{}", Encoding.UTF8, "application/json"), Ct);
        }
    }

    private async Task<ClientProfessionalLink> LinkOfAsync(Actor coach, Actor client)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientProfessionalLinks.AsNoTracking()
            .SingleAsync(l => l.ClientProfileId == client.ProfileId && l.ProfessionalProfileId == coach.ProfileId, Ct);
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
    public async Task Accept_WhenTheLinkEnded_ReactivatesItWithTheInvitesTermsAndAPendingQuestionnaire(bool byToken)
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client)
            .CanViewNutritionPlans(false).CanViewTrainingPlans(false).Inactive().CreateAsync(Ct);
        long questionnaireId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var questionnaire = new Questionnaire { ProfessionalId = coach.UserId, Title = "Intake" };
            db.Questionnaires.Add(questionnaire);
            await db.SaveChangesAsync(Ct);
            questionnaireId = questionnaire.Id;
        }

        var response = await AcceptAsync(byToken, coach, client, questionnaireId: questionnaireId);

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
        var link = await LinkOfAsync(coach, client);
        link.ConnectionState.Should().Be(ConnectionState.Connected);
        link.CanViewTrainingPlans.Should().BeTrue();
        link.QuestionnaireId.Should().Be(questionnaireId);
        using var check = factory.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await checkDb.QuestionnaireResponses.AnyAsync(
            r => r.LinkId == link.Id && r.Status == QuestionnaireResponseStatus.Pending, Ct)).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accept_WhenTheLinkEndedAndTheCoachRoleWasRemoved_Returns403AndKeepsTheLinkEnded(bool byToken)
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        (await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).Inactive().CreateAsync(Ct);

        var response = await AcceptAsync(byToken, coach, client, LinkCapabilityScope.TrainingOnly);

        await AssertCodeAsync(response, HttpStatusCode.Forbidden, ErrorCodes.CoachRoleRemoved);
        (await LinkOfAsync(coach, client)).ConnectionState.Should().Be(ConnectionState.Ended);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accept_WhenTheLinkEndedAndAnotherCoachHoldsTheSlot_Returns400AndKeepsTheLinkEnded(bool byToken)
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var otherCoach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).Inactive().CreateAsync(Ct);
        await TestActors.Link(factory, otherCoach, client).CreateAsync(Ct);

        var response = await AcceptAsync(byToken, coach, client);

        await AssertCodeAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ProfessionAlreadyOccupied);
        (await LinkOfAsync(coach, client)).ConnectionState.Should().Be(ConnectionState.Ended);
    }

    [Fact]
    public async Task AcceptInApp_WhenTermsExistFromAnEarlierCollaboration_BillsFromTheAcceptDayWithoutTheGap()
    {
        var world = await CreateWorldAsync(factory);
        var package = await CreatePackageAsync(world.Coach, 2500m);
        await SetTermsOkAsync(world, package);
        var linkPublicId = (await LinkOfAsync(world.Coach, world.Client)).PublicId;
        (await world.Client.Http.DeleteAsync($"/client/collaborations/{linkPublicId}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var earlierStart = Today.AddMonths(-3);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Make the ended terms and their periods look like they date from three months ago.
            var assignmentId = await db.ClientPaymentAssignments
                .Where(a => a.ClientProfileId == world.Client.ProfileId && a.ProfessionalProfileId == world.Coach.ProfileId)
                .Select(a => a.Id).SingleAsync(Ct);
            foreach (var terms in await db.ClientPaymentTerms.Where(t => t.ClientPaymentAssignmentId == assignmentId).ToListAsync(Ct))
            {
                terms.EffectiveFromUtc = PaymentTermsAging.ShiftMonths(terms.EffectiveFromUtc, -3);
                terms.EndsAtUtc = terms.EndsAtUtc is { } endsAt ? PaymentTermsAging.ShiftMonths(endsAt, -3) : null;
            }

            foreach (var period in await db.ClientPayments.Where(p => p.ClientPaymentAssignmentId == assignmentId).ToListAsync(Ct))
            {
                period.PeriodStart = period.PeriodStart!.Value.AddMonths(-3);
                period.PeriodEnd = period.PeriodEnd!.Value.AddMonths(-3);
                period.DueDate = period.DueDate.AddMonths(-3);
                period.VariableSymbol = null;
            }

            await db.SaveChangesAsync(Ct);
        }

        var invite = await world.Coach.Http.PostAsJsonAsync(
            "/trainer/pending-invites", new { Email = world.Client.Email, PackageId = package }, Ct);
        invite.StatusCode.Should().Be(HttpStatusCode.OK, await invite.Content.ReadAsStringAsync(Ct));
        var inviteId = (await invite.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("publicId").GetGuid();

        var response = await world.Client.Http.PostAsync(
            $"/client/invites/{inviteId}/accept", new StringContent("{}", Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LinkOfAsync(world.Coach, world.Client)).ConnectionState.Should().Be(ConnectionState.Connected);
        using var check = factory.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var starts = await checkDb.ClientPayments.AsNoTracking()
            .Where(p => p.ClientPaymentAssignment.ProfessionalProfileId == world.Coach.ProfileId
                && p.ClientPaymentAssignment.ClientProfileId == world.Client.ProfileId
                && p.PeriodStart != null)
            .OrderBy(p => p.PeriodStart)
            .Select(p => p.PeriodStart!.Value)
            .ToListAsync(Ct);
        // The ended package never resumes: the earlier period stays, nothing bills the months in between, and
        // the invite's package starts on the accept day.
        starts.Should().Equal(earlierStart, Today, Today.AddMonths(1));
    }
}
