using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Questionnaires;

/// <summary>
/// Integration tests for GET /trainer/clients/{clientPublicId}/questionnaire-response —
/// verifies the client-professional link existence check honours <c>IsActive</c>
/// (issue #657: an ex-trainer with a deactivated link must no longer read a
/// former client's questionnaire response).
/// </summary>
[Collection(TestCollection.Name)]
public class GetClientResponseEndpointTests(FitnessApiFactory factory)
{
    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@{tag}-gcr.com";

    // ── Setup helpers ──────────────────────────────────────────────────────────

    private async Task<(HttpClient Http, Guid UserId)> SetupTrainerAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("trainer");
        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Coach", "GCRTest", "Trainer");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
        return (http, user.Id);
    }

    private async Task<Guid> SetupClientAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("client");
        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Test", "GCRTest", "Client");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
        return user.Id;
    }

    private async Task<(long LinkId, Guid ClientPublicId)> InsertLinkAsync(
        Guid clientUserId, Guid professionalUserId, bool isActive)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profProfile = await db.ProfessionalProfiles
            .FirstAsync(p => p.UserId == professionalUserId, TestContext.Current.CancellationToken);
        var clientProfile = await db.ClientProfiles
            .FirstAsync(p => p.UserId == clientUserId, TestContext.Current.CancellationToken);

        var link = new ClientProfessionalLink
        {
            ClientProfileId = clientProfile.Id,
            ProfessionalProfileId = profProfile.Id,
            ProfessionalRole = UserRole.Trainer,
            ConnectionState = isActive ? ConnectionState.Connected : ConnectionState.Ended,
            PublicId = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow,
        };
        db.ClientProfessionalLinks.Add(link);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (link.Id, clientProfile.PublicId);
    }

    private async Task<Guid> InsertSubmittedResponseAsync(
        long linkId, Guid clientUserId, Guid professionalUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var questionnaire = new Questionnaire
        {
            PublicId = Guid.NewGuid(),
            ProfessionalId = professionalUserId,
            Title = "Onboarding",
            IsActive = true,
            DateCreated = DateTime.UtcNow,
        };
        db.Questionnaires.Add(questionnaire);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var response = new QuestionnaireResponse
        {
            PublicId = Guid.NewGuid(),
            QuestionnaireId = questionnaire.Id,
            ClientId = clientUserId,
            ProfessionalId = professionalUserId,
            LinkId = linkId,
            Status = QuestionnaireResponseStatus.Submitted,
            SubmittedAt = DateTime.UtcNow,
            DateCreated = DateTime.UtcNow,
        };
        db.QuestionnaireResponses.Add(response);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return response.PublicId;
    }

    /// <summary>
    /// Inserts a submitted response for a questionnaire with two sections
    /// (#713): "Basic Info" (short_text + number questions) and "Health"
    /// (a multi_select question), each answered.
    /// </summary>
    private async Task<Guid> InsertSubmittedResponseWithSectionsAsync(
        long linkId, Guid clientUserId, Guid professionalUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var questionnaire = new Questionnaire
        {
            PublicId = Guid.NewGuid(),
            ProfessionalId = professionalUserId,
            Title = "Onboarding With Sections",
            IsActive = true,
            DateCreated = DateTime.UtcNow,
        };
        db.Questionnaires.Add(questionnaire);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var basicInfoSection = new QuestionnaireQuestion
        {
            QuestionnaireId = questionnaire.Id, OrderIndex = 0, Type = "section", Label = "Basic Info",
            PublicId = Guid.NewGuid(), DateCreated = DateTime.UtcNow,
        };
        var goalQuestion = new QuestionnaireQuestion
        {
            QuestionnaireId = questionnaire.Id, OrderIndex = 1, Type = "short_text", Label = "Goal",
            PublicId = Guid.NewGuid(), DateCreated = DateTime.UtcNow,
        };
        var weightQuestion = new QuestionnaireQuestion
        {
            QuestionnaireId = questionnaire.Id, OrderIndex = 2, Type = "number", Label = "Weight",
            PublicId = Guid.NewGuid(), DateCreated = DateTime.UtcNow,
        };
        var healthSection = new QuestionnaireQuestion
        {
            QuestionnaireId = questionnaire.Id, OrderIndex = 3, Type = "section", Label = "Health",
            PublicId = Guid.NewGuid(), DateCreated = DateTime.UtcNow,
        };
        var injuriesQuestion = new QuestionnaireQuestion
        {
            QuestionnaireId = questionnaire.Id, OrderIndex = 4, Type = "multi_select", Label = "Injuries",
            PublicId = Guid.NewGuid(), DateCreated = DateTime.UtcNow,
        };
        db.QuestionnaireQuestions.AddRange(
            basicInfoSection, goalQuestion, weightQuestion, healthSection, injuriesQuestion);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var response = new QuestionnaireResponse
        {
            PublicId = Guid.NewGuid(),
            QuestionnaireId = questionnaire.Id,
            ClientId = clientUserId,
            ProfessionalId = professionalUserId,
            LinkId = linkId,
            Status = QuestionnaireResponseStatus.Submitted,
            SubmittedAt = DateTime.UtcNow,
            DateCreated = DateTime.UtcNow,
        };
        db.QuestionnaireResponses.Add(response);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.QuestionnaireAnswers.AddRange(
            new QuestionnaireAnswer { ResponseId = response.Id, QuestionId = goalQuestion.Id, ValueText = "Lose weight", DateCreated = DateTime.UtcNow },
            new QuestionnaireAnswer { ResponseId = response.Id, QuestionId = weightQuestion.Id, ValueNumber = 82, DateCreated = DateTime.UtcNow },
            new QuestionnaireAnswer { ResponseId = response.Id, QuestionId = injuriesQuestion.Id, ValueJson = "[\"knee\"]", DateCreated = DateTime.UtcNow });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return response.PublicId;
    }

    // ── #713: answers resolve to the correct section ─────────────────────────

    [Fact]
    public async Task GetResponse_QuestionnaireWithTwoSections_AnswersResolveToCorrectSection()
    {
        var (trainerHttp, trainerId) = await SetupTrainerAsync();
        var clientId = await SetupClientAsync();

        var (linkId, clientPublicId) = await InsertLinkAsync(clientId, trainerId, isActive: true);
        await InsertSubmittedResponseWithSectionsAsync(linkId, clientId, trainerId);

        var response = await trainerHttp.GetAsync(
            $"/trainer/clients/{clientPublicId}/questionnaire-response",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ResponseWithSectionsBody>(
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        body!.Answers.Should().HaveCount(3);

        var goal = body.Answers.Single(a => a.QuestionLabel == "Goal");
        goal.SectionLabel.Should().Be("Basic Info");
        goal.SectionOrder.Should().Be(0);

        var weight = body.Answers.Single(a => a.QuestionLabel == "Weight");
        weight.SectionLabel.Should().Be("Basic Info");
        weight.SectionOrder.Should().Be(0);

        var injuries = body.Answers.Single(a => a.QuestionLabel == "Injuries");
        injuries.SectionLabel.Should().Be("Health");
        injuries.SectionOrder.Should().Be(1);
    }

    // ── Regression: inactive link → 404, not the answers ──────────────────────

    [Fact]
    public async Task GetResponse_ExTrainerWithInactiveLink_Returns404()
    {
        var (trainerHttp, trainerId) = await SetupTrainerAsync();
        var clientId = await SetupClientAsync();

        var (linkId, clientPublicId) = await InsertLinkAsync(clientId, trainerId, isActive: false);
        await InsertSubmittedResponseAsync(linkId, clientId, trainerId);

        var response = await trainerHttp.GetAsync(
            $"/trainer/clients/{clientPublicId}/questionnaire-response",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Regression guard: no link at all → 404 (pre-existing behaviour) ───────

    [Fact]
    public async Task GetResponse_NoLinkBetweenTrainerAndClient_Returns404()
    {
        var (trainerHttp, _) = await SetupTrainerAsync();
        var clientId = await SetupClientAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clientProfile = await db.ClientProfiles
            .FirstAsync(p => p.UserId == clientId, TestContext.Current.CancellationToken);

        var response = await trainerHttp.GetAsync(
            $"/trainer/clients/{clientProfile.PublicId}/questionnaire-response",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Happy path: active link → 200 with the submitted answers ─────────────

    [Fact]
    public async Task GetResponse_TrainerWithActiveLink_Returns200()
    {
        var (trainerHttp, trainerId) = await SetupTrainerAsync();
        var clientId = await SetupClientAsync();

        var (linkId, clientPublicId) = await InsertLinkAsync(clientId, trainerId, isActive: true);
        var responsePublicId = await InsertSubmittedResponseAsync(linkId, clientId, trainerId);

        var response = await trainerHttp.GetAsync(
            $"/trainer/clients/{clientPublicId}/questionnaire-response",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ResponseBody>(
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        body!.ResponsePublicId.Should().Be(responsePublicId);
        body.QuestionnaireTitle.Should().Be("Onboarding");
    }

    // ── Response shape helper ──────────────────────────────────────────────────

    private record ResponseBody(Guid ResponsePublicId, string QuestionnaireTitle);

    private record SectionedAnswer(string QuestionLabel, string? SectionLabel, int? SectionOrder);

    private record ResponseWithSectionsBody(Guid ResponsePublicId, List<SectionedAnswer> Answers);
}
