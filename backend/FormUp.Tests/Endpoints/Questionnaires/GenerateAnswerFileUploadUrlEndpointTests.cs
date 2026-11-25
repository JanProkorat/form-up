using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Questionnaires.GenerateAnswerFileUploadUrl;
using FormUp.Application.Features.Questionnaires.Shared;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.Questionnaires;

/// <summary>Integration tests for POST /client/questionnaire/response/{id}/file-upload-url.</summary>
[Collection(TestCollection.Name)]
public class GenerateAnswerFileUploadUrlEndpointTests(FitnessApiFactory factory)
{
    private static readonly SeedQuestion[] Form =
    [
        new("file", QuestionTypes.FileUpload),
        new("text", QuestionTypes.ShortText),
        new("section", QuestionTypes.Section),
        new("hidden", QuestionTypes.FileUpload, Hidden: true),
    ];

    private static Task<HttpResponseMessage> PostAsync(
        Actor caller, Guid responseId, Guid questionId, string contentType = "image/png", long sizeBytes = 1024) =>
        caller.Http.PostAsJsonAsync(
            $"/client/questionnaire/response/{responseId}/file-upload-url",
            new { questionPublicId = questionId, contentType, sizeBytes },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Post_FileQuestion_ReturnsUploadUrlStagedUnderTheCallersPath()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);

        var response = await PostAsync(scenario.Client, scenario.ResponsePublicId, scenario.QuestionIds["file"]);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GenerateAnswerFileUploadUrlResponse>(
            TestContext.Current.CancellationToken);
        body!.UploadId.Should().NotBeEmpty();
        body.UploadUrl.Should().Contain(
            QuestionnaireFilePolicy.BuildStagingContainerPath(
                scenario.ResponsePublicId, scenario.Client.UserId, body.UploadId));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("section")]
    [InlineData("hidden")]
    public async Task Post_NotAnAnswerableFileQuestion_Returns400(string key)
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);

        var response = await PostAsync(scenario.Client, scenario.ResponsePublicId, scenario.QuestionIds[key]);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain(key == "text" ? ErrorCodes.QuestionnaireAnswerInvalid : ErrorCodes.QuestionnaireQuestionNotInForm);
    }

    [Fact]
    public async Task Post_QuestionOfAnotherQuestionnaire_Returns400()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);
        var other = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);

        var response = await PostAsync(scenario.Client, scenario.ResponsePublicId, other.QuestionIds["file"]);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(ErrorCodes.QuestionnaireQuestionNotInForm);
    }

    [Fact]
    public async Task Post_ResponseOfAnotherClient_Returns403()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);
        var stranger = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);

        var response = await PostAsync(stranger, scenario.ResponsePublicId, scenario.QuestionIds["file"]);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Post_UnknownResponse_Returns404()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);

        var response = await PostAsync(scenario.Client, Guid.NewGuid(), scenario.QuestionIds["file"]);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Post_SubmittedResponse_Returns409()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(
            factory, Form, status: QuestionnaireResponseStatus.Submitted);

        var response = await PostAsync(scenario.Client, scenario.ResponsePublicId, scenario.QuestionIds["file"]);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Post_DisallowedContentType_Returns400WithCode()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);

        var response = await PostAsync(
            scenario.Client, scenario.ResponsePublicId, scenario.QuestionIds["file"], contentType: "application/pdf");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(ErrorCodes.InvalidImageContentType);
    }
}
