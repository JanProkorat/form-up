using System.Net;
using System.Text;
using System.Text.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Questionnaires;

/// <summary>
/// A coach holding both roles sends one questionnaire per role: each accepted invite keeps its own response,
/// tagged with the scope it granted, and the client sees and answers them one by one.
/// </summary>
[Collection(TestCollection.Name)]
public class QuestionnairePerRoleIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Questionnaire> SeedQuestionnaireAsync(Actor coach, string title, bool isDefault = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var questionnaire = EntityBuilder.Questionnaire.WithProfessionalUserId(coach.UserId).WithTitle(title).Build();
        questionnaire.IsDefault = isDefault;
        db.Questionnaires.Add(questionnaire);
        await db.SaveChangesAsync(Ct);

        return questionnaire;
    }

    private async Task<HttpResponseMessage> AcceptInAppAsync(
        Actor coach, Actor client, LinkCapabilityScope scope, long? questionnaireId)
    {
        using var serviceScope = factory.Services.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var invite = EntityBuilder.PendingInvite
            .WithProfessionalProfileId(coach.ProfileId)
            .WithEmail(client.Email)
            .WithRequestedScope(scope)
            .Build();
        invite.PublicId = Guid.NewGuid();
        invite.QuestionnaireId = questionnaireId;
        db.PendingInvites.Add(invite);
        await db.SaveChangesAsync(Ct);

        return await client.Http.PostAsync(
            $"/client/invites/{invite.PublicId}/accept", new StringContent("{}", Encoding.UTF8, "application/json"), Ct);
    }

    private async Task<List<QuestionnaireResponse>> ResponsesOfAsync(Actor client)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.QuestionnaireResponses.AsNoTracking()
            .Where(r => r.ClientId == client.UserId)
            .OrderBy(r => r.Id)
            .ToListAsync(Ct);
    }

    private static async Task<List<JsonElement>> PendingItemsAsync(Actor client)
    {
        var response = await client.Http.GetAsync("/client/questionnaires/pending", Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);

        return JsonDocument.Parse(raw).RootElement.GetProperty("items").EnumerateArray().ToList();
    }

    private async Task<(Actor Coach, Actor Client, Questionnaire Training, Questionnaire Nutrition)> TrainThenNutritionAsync()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var training = await SeedQuestionnaireAsync(coach, "Training intake");
        var nutrition = await SeedQuestionnaireAsync(coach, "Nutrition intake");

        (await AcceptInAppAsync(coach, client, LinkCapabilityScope.TrainingOnly, training.Id))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await AcceptInAppAsync(coach, client, LinkCapabilityScope.NutritionOnly, nutrition.Id))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        return (coach, client, training, nutrition);
    }

    [Fact]
    public async Task Pending_TrainingThenNutritionInviteWithTwoQuestionnaires_ListsBothWithTheirScope()
    {
        var (_, client, training, nutrition) = await TrainThenNutritionAsync();

        var items = await PendingItemsAsync(client);

        items.Should().HaveCount(2);
        items.Select(i => i.GetProperty("questionnairePublicId").GetGuid())
            .Should().BeEquivalentTo([training.PublicId, nutrition.PublicId]);
        items.Single(i => i.GetProperty("questionnairePublicId").GetGuid() == training.PublicId)
            .GetProperty("scope").GetString().Should().Be("TrainingOnly");
        items.Single(i => i.GetProperty("questionnairePublicId").GetGuid() == nutrition.PublicId)
            .GetProperty("scope").GetString().Should().Be("NutritionOnly");
        items.Should().OnlyContain(i => i.GetProperty("responseStatus").GetString() == "Pending");
    }

    [Fact]
    public async Task Accept_WideningInviteWithQuestionnaire_AddsPendingResponseWithOnlyTheAddedScope()
    {
        var (_, client, _, _) = await TrainThenNutritionAsync();

        var responses = await ResponsesOfAsync(client);

        responses.Should().HaveCount(2);
        responses.Select(r => r.Scope).Should().BeEquivalentTo(
            [LinkCapabilityScope.TrainingOnly, LinkCapabilityScope.NutritionOnly]);
        responses.Should().OnlyContain(r => r.Status == QuestionnaireResponseStatus.Pending);
        responses.Should().OnlyContain(r => r.Kind == QuestionnaireResponseKind.Onboarding);
    }

    [Fact]
    public async Task AcceptByToken_WideningInviteWithQuestionnaire_AddsPendingResponseWithOnlyTheAddedScope()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var training = await SeedQuestionnaireAsync(coach, "Training intake");
        var nutrition = await SeedQuestionnaireAsync(coach, "Nutrition intake");
        await AcceptInAppAsync(coach, client, LinkCapabilityScope.TrainingOnly, training.Id);
        var token = $"token-{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var invite = EntityBuilder.PendingInvite
                .WithProfessionalProfileId(coach.ProfileId)
                .WithEmail(client.Email)
                .WithRequestedScope(LinkCapabilityScope.NutritionOnly)
                .Build();
            invite.PublicId = Guid.NewGuid();
            invite.QuestionnaireId = nutrition.Id;
            db.PendingInvites.Add(invite);
            db.InvitationTokens.Add(EntityBuilder.InvitationToken
                .WithProfessionalProfileId(coach.ProfileId)
                .WithEmail(client.Email)
                .WithToken(token)
                .WithRequestedScope(LinkCapabilityScope.NutritionOnly)
                .Build());
            await db.SaveChangesAsync(Ct);
        }

        var response = await client.Http.PostAsync(
            "/auth/invite/accept",
            new StringContent(JsonSerializer.Serialize(new { Token = token }), Encoding.UTF8, "application/json"),
            Ct);

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
        var responses = await ResponsesOfAsync(client);
        responses.Should().HaveCount(2);
        responses.Single(r => r.QuestionnaireId == nutrition.Id).Scope.Should().Be(LinkCapabilityScope.NutritionOnly);
        responses.Single(r => r.QuestionnaireId == training.Id).Scope.Should().Be(LinkCapabilityScope.TrainingOnly);
        responses.Should().OnlyContain(r => r.Kind == QuestionnaireResponseKind.Onboarding);
    }

    [Fact]
    public async Task Accept_WideningInviteWithoutQuestionnaire_AddsNoResponseAndKeepsTheFirstItem()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var training = await SeedQuestionnaireAsync(coach, "Training intake");
        await AcceptInAppAsync(coach, client, LinkCapabilityScope.TrainingOnly, training.Id);

        (await AcceptInAppAsync(coach, client, LinkCapabilityScope.NutritionOnly, null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ResponsesOfAsync(client)).Should().ContainSingle();
        var items = await PendingItemsAsync(client);
        items.Should().ContainSingle();
        items[0].GetProperty("questionnairePublicId").GetGuid().Should().Be(training.PublicId);
        items[0].GetProperty("scope").GetString().Should().Be("TrainingOnly");
    }

    [Fact]
    public async Task Pending_AfterTheFirstQuestionnaireIsSubmitted_StillListsTheSecond()
    {
        var (_, client, training, nutrition) = await TrainThenNutritionAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var first = await db.QuestionnaireResponses.SingleAsync(
                r => r.ClientId == client.UserId && r.QuestionnaireId == training.Id, Ct);
            first.Status = QuestionnaireResponseStatus.Submitted;
            first.SubmittedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        var items = await PendingItemsAsync(client);

        items.Should().ContainSingle();
        items[0].GetProperty("questionnairePublicId").GetGuid().Should().Be(nutrition.PublicId);
        items[0].GetProperty("scope").GetString().Should().Be("NutritionOnly");
    }

    [Fact]
    public async Task Pending_LinkWithoutResponses_FallsBackToTheLinkQuestionnaireWithNullScope()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var questionnaire = await SeedQuestionnaireAsync(coach, "Default intake", isDefault: true);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);

        var items = await PendingItemsAsync(client);

        items.Should().ContainSingle();
        items[0].GetProperty("questionnairePublicId").GetGuid().Should().Be(questionnaire.PublicId);
        items[0].GetProperty("scope").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Accept_CreatedLinkWithQuestionnaire_StoresTheGrantedScopeOnTheResponse()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var questionnaire = await SeedQuestionnaireAsync(coach, "Both intake");

        (await AcceptInAppAsync(coach, client, LinkCapabilityScope.Both, questionnaire.Id))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ResponsesOfAsync(client)).Should().ContainSingle()
            .Which.Scope.Should().Be(LinkCapabilityScope.Both);
    }

    [Fact]
    public async Task AcceptClientRequest_WithQuestionnaire_StoresTheGrantedScopeOnTheResponse()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var questionnaire = await SeedQuestionnaireAsync(coach, "Request intake");
        Guid requestPublicId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var request = new ClientRequest
            {
                ClientProfileId = client.ProfileId,
                ProfessionalProfileId = coach.ProfileId,
            };
            db.ClientRequests.Add(request);
            await db.SaveChangesAsync(Ct);
            requestPublicId = request.PublicId;
        }

        var response = await coach.Http.PostAsync(
            $"/trainer/client-requests/{requestPublicId}/accept",
            new StringContent(
                JsonSerializer.Serialize(new
                {
                    QuestionnairePublicId = questionnaire.PublicId,
                    RequestedScope = LinkCapabilityScope.NutritionOnly,
                }),
                Encoding.UTF8, "application/json"),
            Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
        var stored = (await ResponsesOfAsync(client)).Should().ContainSingle().Subject;
        stored.Scope.Should().Be(LinkCapabilityScope.NutritionOnly);
        stored.Kind.Should().Be(QuestionnaireResponseKind.Onboarding);
    }

    [Fact]
    public async Task GetQuestionnaire_WithResponsePublicId_LoadsThatResponsesQuestionnaire()
    {
        var (_, client, training, nutrition) = await TrainThenNutritionAsync();
        var nutritionResponse = (await ResponsesOfAsync(client)).Single(r => r.QuestionnaireId == nutrition.Id);

        var response = await client.Http.GetAsync($"/client/questionnaire?responsePublicId={nutritionResponse.PublicId}", Ct);

        var raw = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);
        var root = JsonDocument.Parse(raw).RootElement;
        root.GetProperty("questionnairePublicId").GetGuid().Should().Be(nutrition.PublicId);
        root.GetProperty("questionnairePublicId").GetGuid().Should().NotBe(training.PublicId);
        root.GetProperty("existingResponsePublicId").GetGuid().Should().Be(nutritionResponse.PublicId);
    }

    [Fact]
    public async Task GetQuestionnaire_ResponseOfAnotherClient_Returns404()
    {
        var (_, otherClient, _, _) = await TrainThenNutritionAsync();
        var (_, firstClient, _, _) = await TrainThenNutritionAsync();
        var foreign = (await ResponsesOfAsync(firstClient)).First();

        var response = await otherClient.Http.GetAsync($"/client/questionnaire?responsePublicId={foreign.PublicId}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(QuestionnaireResponseStatus.Submitted)]
    [InlineData(QuestionnaireResponseStatus.Cancelled)]
    public async Task GetQuestionnaire_ClosedResponse_Returns404(QuestionnaireResponseStatus status)
    {
        var (_, client, _, _) = await TrainThenNutritionAsync();
        var target = (await ResponsesOfAsync(client)).First();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var tracked = await db.QuestionnaireResponses.SingleAsync(r => r.Id == target.Id, Ct);
            tracked.Status = status;
            await db.SaveChangesAsync(Ct);
        }

        var response = await client.Http.GetAsync($"/client/questionnaire?responsePublicId={target.PublicId}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetQuestionnaire_ResponseOnAnInactiveLink_Returns404()
    {
        var (_, client, _, _) = await TrainThenNutritionAsync();
        var target = (await ResponsesOfAsync(client)).First();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var link = await db.ClientProfessionalLinks.SingleAsync(l => l.Id == target.LinkId, Ct);
            link.ConnectionState = ConnectionState.Ended;
            await db.SaveChangesAsync(Ct);
        }

        var response = await client.Http.GetAsync($"/client/questionnaire?responsePublicId={target.PublicId}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SubmittedAndCoachLists_CarryTheResponseScope()
    {
        var (coach, client, training, _) = await TrainThenNutritionAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var first = await db.QuestionnaireResponses.SingleAsync(
                r => r.ClientId == client.UserId && r.QuestionnaireId == training.Id, Ct);
            first.Status = QuestionnaireResponseStatus.Submitted;
            first.SubmittedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        var submittedRaw = await (await client.Http.GetAsync("/client/questionnaires/submitted", Ct))
            .Content.ReadAsStringAsync(Ct);
        var submitted = JsonDocument.Parse(submittedRaw).RootElement
            .GetProperty("coaches")[0].GetProperty("responses");
        submitted.GetArrayLength().Should().Be(1);
        submitted[0].GetProperty("scope").GetString().Should().Be("TrainingOnly");

        var coachRaw = await (await coach.Http.GetAsync(
                $"/trainer/clients/{client.PublicId}/questionnaire-responses", Ct))
            .Content.ReadAsStringAsync(Ct);
        var scopes = JsonDocument.Parse(coachRaw).RootElement.GetProperty("responses").EnumerateArray()
            .Select(r => r.GetProperty("scope").GetString()).ToList();
        scopes.Should().BeEquivalentTo(["TrainingOnly", "NutritionOnly"]);
    }
}
