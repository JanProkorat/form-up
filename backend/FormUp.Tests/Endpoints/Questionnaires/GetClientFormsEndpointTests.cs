using System.Net;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Questionnaires.Shared;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Questionnaires.GetClientForms;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Questionnaires;

/// <summary>
/// GET /trainer/clients/{clientPublicId}/forms: the coach's forms for a linked client, split by collaboration.
/// </summary>
[Collection(TestCollection.Shard3)]
public class GetClientFormsEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Actor Coach, Actor Client, long LinkId, Questionnaire Form)> SetupAsync(DateTime? activatedAtUtc)
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var link = TestActors.Link(factory, coach, client);

        if (activatedAtUtc.HasValue)
        {
            link.WithActivatedAtUtc(activatedAtUtc.Value);
        }

        var linkId = await link.CreateAsync(Ct);
        var form = EntityBuilder.Questionnaire
            .WithProfessionalUserId(coach.UserId)
            .WithTitle("Intake")
            .WithQuestion("section", "About you")
            .WithQuestion("short_text", "Main goal")
            .Build();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Questionnaires.Add(form);
        await db.SaveChangesAsync(Ct);

        return (coach, client, linkId, form);
    }

    private async Task<Guid> InsertAsync(QuestionnaireResponse response, DateTime? backdatedCreatedAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.QuestionnaireResponses.Add(response);
        await db.SaveChangesAsync(Ct);

        if (backdatedCreatedAt.HasValue)
        {
            await db.QuestionnaireResponses.Where(r => r.Id == response.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.DateCreated, backdatedCreatedAt.Value), Ct);
        }

        return response.PublicId;
    }

    private QuestionnaireResponseBuilder ResponseFor(
        Actor coach, Actor client, long linkId, Questionnaire form, QuestionnaireResponseStatus status,
        DateTime? submittedAt = null) =>
        EntityBuilder.QuestionnaireResponse
            .ForQuestionnaire(form.Id)
            .OnLink(linkId)
            .FromClient(client.UserId)
            .FromProfessional(coach.UserId)
            .WithStatus(status, submittedAt);

    private static async Task<GetClientFormsResponse> GetFormsAsync(Actor coach, Actor client)
    {
        var response = await coach.Http.GetAsync($"/trainer/clients/{client.PublicId}/forms", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        return (await response.Content.ReadFromJsonAsync<GetClientFormsResponse>(JsonOptions, Ct))!;
    }

    [Fact]
    public async Task GetForms_ReactivatedLink_SplitsCurrentFromPreviousAndOmitsOldUnanswered()
    {
        var (coach, client, linkId, form) = await SetupAsync(DateTime.UtcNow.AddDays(-5));
        var oldAnswered = await InsertAsync(ResponseFor(
            coach, client, linkId, form, QuestionnaireResponseStatus.Submitted, DateTime.UtcNow.AddDays(-9)).Build());
        var oldWaiting = await InsertAsync(
            ResponseFor(coach, client, linkId, form, QuestionnaireResponseStatus.Pending).Build(),
            backdatedCreatedAt: DateTime.UtcNow.AddDays(-10));
        var newAnswered = await InsertAsync(ResponseFor(
            coach, client, linkId, form, QuestionnaireResponseStatus.Submitted, DateTime.UtcNow.AddDays(-1)).Build());
        var newWaiting = await InsertAsync(
            ResponseFor(coach, client, linkId, form, QuestionnaireResponseStatus.InProgress).Build());

        var forms = await GetFormsAsync(coach, client);

        forms.Current.Select(f => f.ResponsePublicId).Should().BeEquivalentTo([newAnswered, newWaiting]);
        forms.Current.Single(f => f.ResponsePublicId == newAnswered).State.Should().Be(FormResponseState.Answered);
        forms.Current.Single(f => f.ResponsePublicId == newWaiting).State.Should().Be(FormResponseState.Waiting);
        forms.Previous.Select(f => f.ResponsePublicId).Should().BeEquivalentTo([oldAnswered]);
        forms.Previous.Concat(forms.Current).Select(f => f.ResponsePublicId).Should().NotContain(oldWaiting);
    }

    [Fact]
    public async Task GetForms_LegacyLinkWithoutActivationTime_ListsEverythingAsCurrentWithAnswers()
    {
        var (coach, client, linkId, form) = await SetupAsync(activatedAtUtc: null);
        var goalQuestionId = form.Questions.Single(q => q.Label == "Main goal").Id;
        var submitted = await InsertAsync(ResponseFor(
                coach, client, linkId, form, QuestionnaireResponseStatus.Submitted, DateTime.UtcNow.AddDays(-30))
            .WithTextAnswer(goalQuestionId, "Run a marathon")
            .Build());
        var waiting = await InsertAsync(
            ResponseFor(coach, client, linkId, form, QuestionnaireResponseStatus.Pending).Build());

        var forms = await GetFormsAsync(coach, client);

        forms.Previous.Should().BeEmpty();
        forms.Current.Select(f => f.ResponsePublicId).Should().BeEquivalentTo([submitted, waiting]);
        var answered = forms.Current.Single(f => f.ResponsePublicId == submitted);
        answered.Kind.Should().Be(QuestionnaireResponseKind.Onboarding);
        answered.QuestionnaireTitle.Should().Be("Intake");
        var answer = answered.Answers.Should().ContainSingle().Subject;
        answer.QuestionLabel.Should().Be("Main goal");
        answer.QuestionType.Should().Be("short_text");
        answer.ValueText.Should().Be("Run a marathon");
        answer.SectionLabel.Should().Be("About you");
        forms.Current.Single(f => f.ResponsePublicId == waiting).Answers.Should().BeEmpty();
    }

    [Fact]
    public async Task GetForms_CheckIns_ReportAnsweredMissedAndWaitingWithTheirWeek()
    {
        var (coach, client, linkId, form) = await SetupAsync(DateTime.UtcNow.AddDays(-60));
        var week = new DateOnly(2026, 10, 26);
        var answered = await InsertAsync(ResponseFor(
                coach, client, linkId, form, QuestionnaireResponseStatus.Submitted, DateTime.UtcNow.AddDays(-2))
            .AsCheckIn(week, DateTime.UtcNow.AddDays(-3)).Build());
        var missed = await InsertAsync(ResponseFor(coach, client, linkId, form, QuestionnaireResponseStatus.Pending)
            .AsCheckIn(week.AddDays(-7), DateTime.UtcNow.AddDays(-1)).Build());
        var waiting = await InsertAsync(ResponseFor(coach, client, linkId, form, QuestionnaireResponseStatus.Pending)
            .AsCheckIn(week.AddDays(7), DateTime.UtcNow.AddDays(1)).Build());

        var forms = await GetFormsAsync(coach, client);

        forms.Current.Should().OnlyContain(f => f.Kind == QuestionnaireResponseKind.CheckIn);
        forms.Current.Single(f => f.ResponsePublicId == answered).State.Should().Be(FormResponseState.Answered);
        forms.Current.Single(f => f.ResponsePublicId == missed).State.Should().Be(FormResponseState.Missed);
        forms.Current.Single(f => f.ResponsePublicId == waiting).State.Should().Be(FormResponseState.Waiting);
        forms.Current.Single(f => f.ResponsePublicId == answered).CheckInWeekStart.Should().Be(week);
    }

    [Theory]
    [InlineData("scale", "{\"min\":1,\"max\":5}", 5)]
    [InlineData("scale", null, null)]
    [InlineData("scale", "{\"min\":1}", null)]
    [InlineData("scale", "{not json", null)]
    [InlineData("number", "{\"min\":0,\"max\":100}", null)]
    public async Task GetForms_NumericAnswer_ReturnsScaleMaxOnlyForAConfiguredScale(
        string questionType, string? config, int? expectedScaleMax)
    {
        var (coach, client, linkId, _) = await SetupAsync(DateTime.UtcNow.AddDays(-5));
        var form = EntityBuilder.Questionnaire
            .WithProfessionalUserId(coach.UserId)
            .WithQuestion(questionType, "Energy")
            .Build();
        var question = form.Questions.Single();
        question.Config = config;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Questionnaires.Add(form);
            await db.SaveChangesAsync(Ct);
        }

        var response = ResponseFor(
            coach, client, linkId, form, QuestionnaireResponseStatus.Submitted, DateTime.UtcNow.AddDays(-1)).Build();
        response.Answers.Add(new QuestionnaireAnswer
        {
            PublicId = Guid.NewGuid(),
            QuestionId = question.Id,
            ValueNumber = 3,
        });
        var responseId = await InsertAsync(response);

        var forms = await GetFormsAsync(coach, client);

        var answer = forms.Current.Single(f => f.ResponsePublicId == responseId).Answers.Should().ContainSingle().Subject;
        answer.QuestionType.Should().Be(questionType);
        answer.ValueNumber.Should().Be(3);
        answer.ScaleMax.Should().Be(expectedScaleMax);
    }

    [Fact]
    public async Task GetForms_UnansweredNonCheckInForm_NeverShowsAsMissed()
    {
        var (coach, client, linkId, form) = await SetupAsync(DateTime.UtcNow.AddDays(-60));
        var waiting = await InsertAsync(
            ResponseFor(coach, client, linkId, form, QuestionnaireResponseStatus.Pending).Build(),
            backdatedCreatedAt: DateTime.UtcNow.AddDays(-40));

        var forms = await GetFormsAsync(coach, client);

        forms.Current.Single(f => f.ResponsePublicId == waiting).State.Should().Be(FormResponseState.Waiting);
    }

    [Fact]
    public async Task GetForms_AnotherCoachsResponseForTheSameClient_IsNotReturned()
    {
        var (coach, client, linkId, form) = await SetupAsync(DateTime.UtcNow.AddDays(-5));
        var mine = await InsertAsync(ResponseFor(coach, client, linkId, form, QuestionnaireResponseStatus.Pending).Build());
        var otherCoach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var otherLinkId = await TestActors.Link(factory, otherCoach, client).AsRole(UserRole.Nutritionist).CreateAsync(Ct);
        var otherForm = EntityBuilder.Questionnaire.WithProfessionalUserId(otherCoach.UserId).Build();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Questionnaires.Add(otherForm);
            await db.SaveChangesAsync(Ct);
        }

        var theirs = await InsertAsync(ResponseFor(
            otherCoach, client, otherLinkId, otherForm, QuestionnaireResponseStatus.Pending).Build());

        var forms = await GetFormsAsync(coach, client);

        forms.Current.Select(f => f.ResponsePublicId).Should().Contain(mine).And.NotContain(theirs);
    }

    [Fact]
    public async Task GetForms_CoachWithoutALiveLink_Returns404()
    {
        var (_, client, _, _) = await SetupAsync(DateTime.UtcNow.AddDays(-5));
        var stranger = await TestActors.Trainer(factory).CreateAsync(Ct);

        var response = await stranger.Http.GetAsync($"/trainer/clients/{client.PublicId}/forms", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetForms_CoachWhoseLinkEnded_Returns404()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).Inactive().CreateAsync(Ct);

        var response = await coach.Http.GetAsync($"/trainer/clients/{client.PublicId}/forms", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetForms_UnknownClient_Returns404()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        var response = await coach.Http.GetAsync($"/trainer/clients/{Guid.NewGuid()}/forms", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetForms_FileAnswer_ReturnsSignedUrlNotTheStoredValue()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(
            factory, [new("file", QuestionTypes.FileUpload)], status: QuestionnaireResponseStatus.Submitted);
        var storedUrl = QuestionnaireFilePolicy.BuildFinalContainerPath(scenario.ResponsePublicId, Guid.NewGuid(), "png");
        await scenario.AddFileAnswerAsync(factory, "file", storedUrl);

        var forms = await GetFormsAsync(scenario.Coach, scenario.Client);

        forms.Current.Should().ContainSingle()
            .Which.Answers.Should().ContainSingle().Which.FileUrl.Should().Be($"{storedUrl}?signed=test");
    }
}
