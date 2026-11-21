using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Features.Questionnaires.GetTrainerQuestionnaire;
using FormUp.Application.Domain.Enums;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.Questionnaires;

/// <summary>
/// Create and update of questionnaires: form type can be changed on update, only Onboarding forms can be default,
/// legacy question configs keep working, and an update moves the list's edited-at.
/// </summary>
[Collection(TestCollection.Shard2)]
public class UpdateQuestionnaireEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static async Task<GetTrainerQuestionnaireResponse> CreateAsync(Actor trainer, string title, string type)
    {
        var response = await trainer.Http.PostAsJsonAsync(
            "/trainer/questionnaires", new { Title = title, Type = type }, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<GetTrainerQuestionnaireResponse>(JsonOptions, TestContext.Current.CancellationToken))!;
    }

    private static Task<HttpResponseMessage> UpdateAsync(
        Actor trainer, Guid publicId, string? type, bool isDefault, object[] questions) =>
        trainer.Http.PutAsJsonAsync(
            $"/trainer/questionnaires/{publicId}",
            new { Title = "Renamed", Type = type, IsActive = true, IsDefault = isDefault, Questions = questions },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Create_CheckInAsFirstForm_IsNotDefault_AndFirstOnboardingStillBecomesDefault()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);

        var checkIn = await CreateAsync(trainer, "Weekly", "CheckIn");
        var onboarding = await CreateAsync(trainer, "Intake", "Onboarding");

        checkIn.Type.Should().Be(QuestionnaireType.CheckIn);
        checkIn.IsDefault.Should().BeFalse();
        onboarding.Type.Should().Be(QuestionnaireType.Onboarding);
        onboarding.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task Create_UndefinedType_Returns400()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);

        var response = await trainer.Http.PostAsJsonAsync(
            "/trainer/questionnaires", new { Title = "Bad", Type = 99 }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_OnboardingToCheckIn_PersistsTypeAndClearsDefault()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var form = await CreateAsync(trainer, "Intake", "Onboarding");
        form.IsDefault.Should().BeTrue();

        var response = await UpdateAsync(trainer, form.PublicId, "CheckIn", isDefault: false, questions: []);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await response.Content.ReadFromJsonAsync<GetTrainerQuestionnaireResponse>(JsonOptions, TestContext.Current.CancellationToken))!;
        updated.Type.Should().Be(QuestionnaireType.CheckIn);
        updated.IsDefault.Should().BeFalse();

        var reloaded = await trainer.Http.GetAsync($"/trainer/questionnaires/{form.PublicId}", TestContext.Current.CancellationToken);
        (await reloaded.Content.ReadFromJsonAsync<GetTrainerQuestionnaireResponse>(JsonOptions, TestContext.Current.CancellationToken))!
            .Type.Should().Be(QuestionnaireType.CheckIn);
    }

    [Fact]
    public async Task Update_CheckInToOnboarding_PersistsType()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var form = await CreateAsync(trainer, "Weekly", "CheckIn");

        var response = await UpdateAsync(trainer, form.PublicId, "Onboarding", isDefault: false, questions: []);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<GetTrainerQuestionnaireResponse>(JsonOptions, TestContext.Current.CancellationToken))!
            .Type.Should().Be(QuestionnaireType.Onboarding);
    }

    [Fact]
    public async Task Update_ChangeToCheckInWithIsDefault_Returns400()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var form = await CreateAsync(trainer, "Intake", "Onboarding");

        var response = await UpdateAsync(trainer, form.PublicId, "CheckIn", isDefault: true, questions: []);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("CHECKIN_FORM_CANNOT_BE_DEFAULT");
    }

    [Fact]
    public async Task Update_SameTypeOrOmittedType_Returns200AndKeepsType()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var form = await CreateAsync(trainer, "Weekly", "CheckIn");

        var sameType = await UpdateAsync(trainer, form.PublicId, "CheckIn", isDefault: false, questions: []);
        var omittedType = await UpdateAsync(trainer, form.PublicId, null, isDefault: false, questions: []);

        sameType.StatusCode.Should().Be(HttpStatusCode.OK);
        omittedType.StatusCode.Should().Be(HttpStatusCode.OK);
        (await omittedType.Content.ReadFromJsonAsync<GetTrainerQuestionnaireResponse>(JsonOptions, TestContext.Current.CancellationToken))!
            .Type.Should().Be(QuestionnaireType.CheckIn);
    }

    [Fact]
    public async Task Update_SetDefaultOnCheckIn_Returns400()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var form = await CreateAsync(trainer, "Weekly", "CheckIn");

        var response = await UpdateAsync(trainer, form.PublicId, "CheckIn", isDefault: true, questions: []);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("CHECKIN_FORM_CANNOT_BE_DEFAULT");
    }

    [Fact]
    public async Task Update_LegacyConfigs_Returns200()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var form = await CreateAsync(trainer, "Intake", "Onboarding");

        var response = await UpdateAsync(trainer, form.PublicId, null, isDefault: true,
        [
            new { OrderIndex = 0, Type = "number", Label = "Weight", Config = "{\"min\":30,\"max\":250,\"unit\":\"kg\",\"step\":1}" },
            new { OrderIndex = 1, Type = "single_choice", Label = "Days", Config = "{\"options\":[\"0\",\"1-2\",\"3-4\"]}" },
            new { OrderIndex = 2, Type = "scale", Label = "Energy", Config = "{\"min\":1,\"max\":10,\"labelMin\":\"Low\",\"labelMax\":\"High\"}" },
            new { OrderIndex = 3, Type = "star_rating", Label = "Sleep", Config = (string?)null },
        ]);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Update_FormWithSectionQuestion_Returns200AndKeepsSection()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var form = await CreateAsync(trainer, "Intake", "Onboarding");

        var response = await UpdateAsync(trainer, form.PublicId, null, isDefault: true,
        [
            new { OrderIndex = 0, Type = "section", Label = "Lifestyle", Config = (string?)null },
            new { OrderIndex = 1, Type = "short_text", Label = "Job", Config = (string?)null },
        ]);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await response.Content.ReadFromJsonAsync<GetTrainerQuestionnaireResponse>(JsonOptions, TestContext.Current.CancellationToken))!;
        updated.Questions.Select(question => question.Type).Should().Equal("section", "short_text");
    }

    [Fact]
    public async Task Update_RequiredSection_Returns400()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var form = await CreateAsync(trainer, "Intake", "Onboarding");

        var response = await UpdateAsync(trainer, form.PublicId, null, isDefault: true,
        [
            new { OrderIndex = 0, Type = "section", Label = "Lifestyle", IsRequired = true },
        ]);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("QUESTION_SECTION_INVALID");
    }

    [Fact]
    public async Task Update_SectionsBetweenQuestions_RoundTripsInOrderWithHelperText()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var form = await CreateAsync(trainer, "Intake", "Onboarding");

        var response = await UpdateAsync(trainer, form.PublicId, null, isDefault: true,
        [
            new { OrderIndex = 0, Type = "section", Label = "About you", HelperText = "Basics first" },
            new { OrderIndex = 1, Type = "short_text", Label = "Job" },
            new { OrderIndex = 2, Type = "section", Label = "Training", HelperText = "How you move" },
            new { OrderIndex = 3, Type = "short_text", Label = "Goal" },
        ]);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var reloaded = await trainer.Http.GetFromJsonAsync<GetTrainerQuestionnaireResponse>(
            $"/trainer/questionnaires/{form.PublicId}", JsonOptions, TestContext.Current.CancellationToken);

        var questions = reloaded!.Questions.OrderBy(question => question.OrderIndex).ToList();
        questions.Select(question => question.Type).Should().Equal("section", "short_text", "section", "short_text");
        questions.Select(question => question.Label).Should().Equal("About you", "Job", "Training", "Goal");
        questions[0].HelperText.Should().Be("Basics first");
        questions[2].HelperText.Should().Be("How you move");
    }

    [Fact]
    public async Task Update_InvalidConfig_Returns400()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var form = await CreateAsync(trainer, "Intake", "Onboarding");

        var response = await UpdateAsync(trainer, form.PublicId, null, isDefault: true,
        [
            new { OrderIndex = 0, Type = "single_choice", Label = "Days", Config = "{\"options\":[]}" },
        ]);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_OtherProfessionalsForm_Returns404()
    {
        var owner = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var intruder = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var form = await CreateAsync(owner, "Intake", "Onboarding");

        var response = await UpdateAsync(intruder, form.PublicId, null, isDefault: false, questions: []);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_MovesEditedAtInTheList()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var form = await CreateAsync(trainer, "Intake", "Onboarding");

        var before = await trainer.Http.GetFromJsonAsync<GetTrainerQuestionnairesResponse>(
            "/trainer/questionnaires", JsonOptions, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Only a question changes; the questionnaire's own columns are identical to the stored ones.
        var update = await trainer.Http.PutAsJsonAsync(
            $"/trainer/questionnaires/{form.PublicId}",
            new
            {
                Title = "Intake",
                Type = "Onboarding",
                IsActive = true,
                IsDefault = true,
                Questions = new object[] { new { OrderIndex = 0, Type = "short_text", Label = "Goal" } },
            },
            TestContext.Current.CancellationToken);
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await trainer.Http.GetFromJsonAsync<GetTrainerQuestionnairesResponse>(
            "/trainer/questionnaires", JsonOptions, TestContext.Current.CancellationToken);

        after!.Questionnaires.Single(q => q.PublicId == form.PublicId).EditedAt
            .Should().BeAfter(before!.Questionnaires.Single(q => q.PublicId == form.PublicId).EditedAt);
    }
}
