using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Questionnaires.GetTrainerQuestionnaire;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Questionnaires;

/// <summary>
/// GET /trainer/questionnaires: type filter, title search, question count, used-for count and edited-at.
/// </summary>
[Collection(TestCollection.Shard4)]
public class GetTrainerQuestionnairesEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private async Task<Questionnaire> SeedQuestionnaireAsync(
        Guid professionalId, string title, QuestionnaireType type, int questionCount = 0, DateTime? dateUpdated = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var questionnaire = new Questionnaire
        {
            PublicId = Guid.NewGuid(),
            ProfessionalId = professionalId,
            Title = title,
            Type = type,
            IsActive = true,
        };

        for (var index = 0; index < questionCount; index++)
        {
            questionnaire.Questions.Add(new QuestionnaireQuestion
            {
                PublicId = Guid.NewGuid(),
                OrderIndex = index,
                Type = "short_text",
                Label = $"Question {index}",
            });
        }

        db.Questionnaires.Add(questionnaire);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        if (dateUpdated.HasValue)
        {
            // Bypass SaveChanges so the timestamping hook does not overwrite the value.
            await db.Questionnaires
                .Where(q => q.Id == questionnaire.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(q => q.DateUpdated, dateUpdated), TestContext.Current.CancellationToken);
        }

        return questionnaire;
    }

    private async Task<GetTrainerQuestionnairesResponse> GetListAsync(Actor trainer, string query = "")
    {
        var response = await trainer.Http.GetAsync($"/trainer/questionnaires{query}", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<GetTrainerQuestionnairesResponse>(JsonOptions, TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task List_TypeFilter_ReturnsOnlyThatType()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var onboarding = await SeedQuestionnaireAsync(trainer.UserId, "Intake", QuestionnaireType.Onboarding);
        var checkIn = await SeedQuestionnaireAsync(trainer.UserId, "Weekly", QuestionnaireType.CheckIn);

        var checkInList = await GetListAsync(trainer, "?type=CheckIn");
        var onboardingList = await GetListAsync(trainer, "?type=Onboarding");
        var allList = await GetListAsync(trainer);

        checkInList.Questionnaires.Select(q => q.PublicId).Should().Equal(checkIn.PublicId);
        checkInList.Questionnaires.Single().Type.Should().Be(QuestionnaireType.CheckIn);
        onboardingList.Questionnaires.Select(q => q.PublicId).Should().Equal(onboarding.PublicId);
        allList.Questionnaires.Select(q => q.PublicId).Should().BeEquivalentTo([onboarding.PublicId, checkIn.PublicId]);
    }

    [Fact]
    public async Task List_Search_MatchesTitleCaseInsensitivelyAndEscapesWildcards()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var match = await SeedQuestionnaireAsync(trainer.UserId, "Weekly Wellness Check", QuestionnaireType.CheckIn);
        await SeedQuestionnaireAsync(trainer.UserId, "Intake", QuestionnaireType.Onboarding);

        var found = await GetListAsync(trainer, "?search=wellNESS");
        var wildcard = await GetListAsync(trainer, "?search=%25");

        found.Questionnaires.Select(q => q.PublicId).Should().Equal(match.PublicId);
        wildcard.Questionnaires.Should().BeEmpty("a literal percent sign is not a wildcard");
    }

    [Fact]
    public async Task List_OtherProfessionalsForms_AreNotReturned()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var other = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        await SeedQuestionnaireAsync(other.UserId, "Not mine", QuestionnaireType.Onboarding);

        var list = await GetListAsync(trainer);

        list.Questionnaires.Should().BeEmpty();
    }

    [Fact]
    public async Task List_UndefinedTypeValue_Returns400()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);

        var response = await trainer.Http.GetAsync("/trainer/questionnaires?type=99", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_ReturnsQuestionCountUsedForCountAndEditedAt()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var editedAt = DateTime.UtcNow.AddDays(-1);
        var edited = await SeedQuestionnaireAsync(trainer.UserId, "Edited", QuestionnaireType.Onboarding, questionCount: 3, dateUpdated: editedAt);
        var untouched = await SeedQuestionnaireAsync(trainer.UserId, "Untouched", QuestionnaireType.CheckIn);

        var clientOne = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var clientTwo = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var clientThree = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var linkOne = await TestActors.Link(factory, trainer, clientOne).CreateAsync(TestContext.Current.CancellationToken);
        var linkTwo = await TestActors.Link(factory, trainer, clientTwo).CreateAsync(TestContext.Current.CancellationToken);
        var linkThree = await TestActors.Link(factory, trainer, clientThree).CreateAsync(TestContext.Current.CancellationToken);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            QuestionnaireResponse Response(Actor client, long linkId, QuestionnaireResponseStatus status) => new()
            {
                QuestionnaireId = edited.Id,
                ClientId = client.UserId,
                ProfessionalId = trainer.UserId,
                LinkId = linkId,
                Status = status,
            };

            // Client one has two responses (counted once); client two only a cancelled one (not counted).
            db.QuestionnaireResponses.AddRange(
                Response(clientOne, linkOne, QuestionnaireResponseStatus.Submitted),
                Response(clientOne, linkOne, QuestionnaireResponseStatus.Pending),
                Response(clientTwo, linkTwo, QuestionnaireResponseStatus.Cancelled),
                Response(clientThree, linkThree, QuestionnaireResponseStatus.InProgress));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var list = await GetListAsync(trainer);

        var editedRow = list.Questionnaires.Single(q => q.PublicId == edited.PublicId);
        editedRow.QuestionCount.Should().Be(3);
        editedRow.UsedForClientCount.Should().Be(2);
        editedRow.EditedAt.Should().BeCloseTo(editedAt, TimeSpan.FromSeconds(1));

        var untouchedRow = list.Questionnaires.Single(q => q.PublicId == untouched.PublicId);
        untouchedRow.QuestionCount.Should().Be(0);
        untouchedRow.UsedForClientCount.Should().Be(0);
        untouchedRow.EditedAt.Should().BeCloseTo(untouchedRow.DateCreated, TimeSpan.FromSeconds(1));
    }

    private static DateOnly LastWeekMonday() =>
        AutomationScheduleCalculator.MondayOf(DateOnly.FromDateTime(DateTime.UtcNow)).AddDays(-7);

    private async Task SeedCheckInRowsAsync(
        Actor trainer, Questionnaire questionnaire, QuestionnaireResponseKind kind,
        params (DateOnly Week, QuestionnaireResponseStatus Status)[] rows)
    {
        foreach (var (week, status) in rows)
        {
            var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
            var linkId = await TestActors.Link(factory, trainer, client).CreateAsync(TestContext.Current.CancellationToken);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            db.QuestionnaireResponses.Add(new QuestionnaireResponse
            {
                QuestionnaireId = questionnaire.Id,
                ClientId = client.UserId,
                ProfessionalId = trainer.UserId,
                LinkId = linkId,
                Status = status,
                Kind = kind,
                CheckInWeekStart = week,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task List_CheckInForm_RateCountsOnlyLastWeekAndRounds()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var form = await SeedQuestionnaireAsync(trainer.UserId, "Weekly", QuestionnaireType.CheckIn);
        var lastWeek = LastWeekMonday();

        await SeedCheckInRowsAsync(trainer, form, QuestionnaireResponseKind.CheckIn,
            (lastWeek, QuestionnaireResponseStatus.Submitted),
            (lastWeek, QuestionnaireResponseStatus.Submitted),
            (lastWeek, QuestionnaireResponseStatus.Pending),
            (lastWeek.AddDays(7), QuestionnaireResponseStatus.Pending),
            (lastWeek.AddDays(-7), QuestionnaireResponseStatus.Pending));

        var list = await GetListAsync(trainer);

        list.Questionnaires.Single(q => q.PublicId == form.PublicId).LastWeekAnswerRatePercent.Should().Be(67);
    }

    [Fact]
    public async Task List_CheckInForm_CancelledRowsAreIgnored()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var form = await SeedQuestionnaireAsync(trainer.UserId, "Weekly", QuestionnaireType.CheckIn);
        var lastWeek = LastWeekMonday();

        await SeedCheckInRowsAsync(trainer, form, QuestionnaireResponseKind.CheckIn,
            (lastWeek, QuestionnaireResponseStatus.Submitted),
            (lastWeek, QuestionnaireResponseStatus.Cancelled),
            (lastWeek, QuestionnaireResponseStatus.Cancelled));

        var list = await GetListAsync(trainer);

        list.Questionnaires.Single(q => q.PublicId == form.PublicId).LastWeekAnswerRatePercent.Should().Be(100);
    }

    [Fact]
    public async Task List_CheckInFormWithoutLastWeekRows_RateIsNull()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var form = await SeedQuestionnaireAsync(trainer.UserId, "Weekly", QuestionnaireType.CheckIn);

        await SeedCheckInRowsAsync(trainer, form, QuestionnaireResponseKind.CheckIn,
            (LastWeekMonday().AddDays(-7), QuestionnaireResponseStatus.Submitted));

        var list = await GetListAsync(trainer);

        list.Questionnaires.Single(q => q.PublicId == form.PublicId).LastWeekAnswerRatePercent.Should().BeNull();
    }

    [Fact]
    public async Task List_NonCheckInForm_RateIsNullEvenWithResponses()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var form = await SeedQuestionnaireAsync(trainer.UserId, "Intake", QuestionnaireType.Onboarding);

        await SeedCheckInRowsAsync(trainer, form, QuestionnaireResponseKind.Questionnaire,
            (LastWeekMonday(), QuestionnaireResponseStatus.Submitted));

        var list = await GetListAsync(trainer);

        list.Questionnaires.Single(q => q.PublicId == form.PublicId).LastWeekAnswerRatePercent.Should().BeNull();
    }
}
