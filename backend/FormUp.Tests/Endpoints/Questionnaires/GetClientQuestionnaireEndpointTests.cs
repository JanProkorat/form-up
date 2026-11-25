using System.Net;
using Microsoft.Extensions.DependencyInjection;
using FormUp.Application.Domain.Interfaces;
using System.Net.Http.Json;
using FormUp.Application.Features.Questionnaires.GetClientQuestionnaire;
using FormUp.Application.Features.Questionnaires.Shared;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.Questionnaires;

/// <summary>Integration tests for GET /client/questionnaire query binding.</summary>
[Collection(TestCollection.Name)]
public class GetClientQuestionnaireEndpointTests(FitnessApiFactory factory)
{
    private static readonly SeedQuestion[] Form = [new("text", QuestionTypes.ShortText)];

    [Fact]
    public async Task Get_WithResponsePublicId_BindsItAndReturnsThatResponse()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);

        var body = await scenario.Client.Http.GetFromJsonAsync<GetClientQuestionnaireResponse>(
            $"/client/questionnaire?responsePublicId={scenario.ResponsePublicId}",
            TestContext.Current.CancellationToken);

        body!.ExistingResponsePublicId.Should().Be(scenario.ResponsePublicId);
        body.QuestionnairePublicId.Should().Be(scenario.QuestionnairePublicId);
    }

    [Fact]
    public async Task Get_WithLinkPublicId_BindsItAndReturnsThatLinksQuestionnaire()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);

        var response = await scenario.Client.Http.GetAsync(
            $"/client/questionnaire?linkPublicId={scenario.LinkPublicId}",
            TestContext.Current.CancellationToken);

        // The seeded questionnaire is the coach's default, so the link resolves to it.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GetClientQuestionnaireResponse>(
            TestContext.Current.CancellationToken);
        body!.LinkPublicId.Should().Be(scenario.LinkPublicId);
    }

    [Fact]
    public async Task Get_WithUnknownResponsePublicId_Returns404()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);

        var response = await scenario.Client.Http.GetAsync(
            $"/client/questionnaire?responsePublicId={Guid.NewGuid()}",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_StoredOwnFileAnswer_ReturnsSignedUrlAndTheUploadId()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, [new("file", QuestionTypes.FileUpload)]);
        var uploadId = Guid.NewGuid();
        var storedUrl = QuestionnaireFilePolicy.BuildFinalContainerPath(scenario.ResponsePublicId, uploadId, "png");
        await scenario.AddFileAnswerAsync(factory, "file", storedUrl);

        var body = await scenario.Client.Http.GetFromJsonAsync<GetClientQuestionnaireResponse>(
            $"/client/questionnaire?responsePublicId={scenario.ResponsePublicId}",
            TestContext.Current.CancellationToken);

        var answer = body!.ExistingAnswers.Should().ContainSingle().Subject;
        answer.FileUrl.Should().Be($"{storedUrl}?signed=test");
        answer.FileUploadId.Should().Be(uploadId);
    }

    [Fact]
    public async Task Get_StoredForeignFileUrl_HasNoUploadId()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, [new("file", QuestionTypes.FileUpload)]);
        await scenario.AddFileAnswerAsync(factory, "file", "https://storage.example.test/legacy/medical.pdf");

        var body = await scenario.Client.Http.GetFromJsonAsync<GetClientQuestionnaireResponse>(
            $"/client/questionnaire?responsePublicId={scenario.ResponsePublicId}",
            TestContext.Current.CancellationToken);

        body!.ExistingAnswers.Should().ContainSingle().Which.FileUploadId.Should().BeNull();
    }
}
