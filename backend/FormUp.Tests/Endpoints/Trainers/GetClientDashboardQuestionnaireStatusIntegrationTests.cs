using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Trainers.GetClientDashboard;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// The client dashboard's onboarding status counts only answers from the current collaboration, so a
/// returning client who has not re-onboarded does not show as submitted.
/// </summary>
[Collection(TestCollection.Shard3)]
public class GetClientDashboardQuestionnaireStatusIntegrationTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private async Task<(Actor Trainer, Actor Client, long LinkId, long QuestionnaireId)> SetupAsync(
        DateTime? activatedAtUtc)
    {
        var trainer = await TestActors.Professional(factory, UserRole.Trainer).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var link = TestActors.Link(factory, trainer, client);

        if (activatedAtUtc.HasValue)
        {
            link.WithActivatedAtUtc(activatedAtUtc.Value);
        }

        var linkId = await link.CreateAsync(Ct);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var questionnaire = EntityBuilder.Questionnaire.WithProfessionalUserId(trainer.UserId).Build();
        db.Questionnaires.Add(questionnaire);
        await db.SaveChangesAsync(Ct);

        return (trainer, client, linkId, questionnaire.Id);
    }

    private async Task<Guid> InsertResponseAsync(
        Actor trainer, Actor client, long linkId, long questionnaireId,
        QuestionnaireResponseStatus status, DateTime? submittedAt = null)
    {
        var response = EntityBuilder.QuestionnaireResponse
            .ForQuestionnaire(questionnaireId)
            .OnLink(linkId)
            .FromClient(client.UserId)
            .FromProfessional(trainer.UserId)
            .WithStatus(status, submittedAt)
            .Build();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.QuestionnaireResponses.Add(response);
        await db.SaveChangesAsync(Ct);
        return response.PublicId;
    }

    /// <summary>Moves the activation time past every response inserted so far, as a re-activation does.</summary>
    private async Task ReactivateLinkAsync(long linkId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var link = await db.ClientProfessionalLinks.FirstAsync(l => l.Id == linkId, Ct);
        link.ActivatedAtUtc = DateTime.UtcNow.AddSeconds(1);
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<GetClientDashboardResponse> DashboardAsync(Actor trainer, Actor client)
    {
        var response = await trainer.Http.GetAsync($"/trainer/clients/{client.PublicId}", Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<GetClientDashboardResponse>(JsonOptions, Ct))!;
    }

    [Fact]
    public async Task GetDashboard_ReturningClientWithOnlyOldSubmittedResponse_ReportsNone()
    {
        var (trainer, client, linkId, questionnaireId) = await SetupAsync(DateTime.UtcNow.AddDays(-30));
        await InsertResponseAsync(trainer, client, linkId, questionnaireId,
            QuestionnaireResponseStatus.Submitted, submittedAt: DateTime.UtcNow.AddDays(-29));
        await ReactivateLinkAsync(linkId);

        var dashboard = await DashboardAsync(trainer, client);

        dashboard.QuestionnaireStatus.Should().Be("none");
        dashboard.QuestionnaireTitle.Should().BeNull();
        dashboard.QuestionnaireResponsePublicId.Should().BeNull();
        dashboard.QuestionnaireSubmittedAt.Should().BeNull();
    }

    [Fact]
    public async Task GetDashboard_ReactivatedLinkWithOldInProgressResponse_ReportsNone()
    {
        var (trainer, client, linkId, questionnaireId) = await SetupAsync(DateTime.UtcNow.AddDays(-30));
        await InsertResponseAsync(trainer, client, linkId, questionnaireId, QuestionnaireResponseStatus.InProgress);
        await ReactivateLinkAsync(linkId);

        var dashboard = await DashboardAsync(trainer, client);

        dashboard.QuestionnaireStatus.Should().Be("none");
        dashboard.QuestionnaireTitle.Should().BeNull();
        dashboard.QuestionnaireResponsePublicId.Should().BeNull();
        dashboard.QuestionnaireSubmittedAt.Should().BeNull();
    }

    [Fact]
    public async Task GetDashboard_SubmittedInCurrentCollaboration_ReportsSubmitted()
    {
        var (trainer, client, linkId, questionnaireId) = await SetupAsync(DateTime.UtcNow.AddDays(-30));
        var submittedAt = DateTime.UtcNow.AddDays(-1);
        var responseId = await InsertResponseAsync(trainer, client, linkId, questionnaireId,
            QuestionnaireResponseStatus.Submitted, submittedAt);

        var dashboard = await DashboardAsync(trainer, client);

        dashboard.QuestionnaireStatus.Should().Be("submitted");
        dashboard.QuestionnaireTitle.Should().Be("Onboarding");
        dashboard.QuestionnaireResponsePublicId.Should().Be(responseId);
        dashboard.QuestionnaireSubmittedAt.Should().BeCloseTo(submittedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetDashboard_InProgressInCurrentCollaboration_ReportsPending()
    {
        var (trainer, client, linkId, questionnaireId) = await SetupAsync(DateTime.UtcNow.AddDays(-30));
        var responseId = await InsertResponseAsync(trainer, client, linkId, questionnaireId,
            QuestionnaireResponseStatus.InProgress);

        var dashboard = await DashboardAsync(trainer, client);

        dashboard.QuestionnaireStatus.Should().Be("pending");
        dashboard.QuestionnaireResponsePublicId.Should().Be(responseId);
        dashboard.QuestionnaireSubmittedAt.Should().BeNull();
    }

    [Fact]
    public async Task GetDashboard_LegacyLinkWithoutActivationTime_EarlierSubmissionStillReportsSubmitted()
    {
        var (trainer, client, linkId, questionnaireId) = await SetupAsync(activatedAtUtc: null);
        var responseId = await InsertResponseAsync(trainer, client, linkId, questionnaireId,
            QuestionnaireResponseStatus.Submitted, submittedAt: DateTime.UtcNow.AddDays(-29));

        var dashboard = await DashboardAsync(trainer, client);

        dashboard.QuestionnaireStatus.Should().Be("submitted");
        dashboard.QuestionnaireResponsePublicId.Should().Be(responseId);
    }
}
