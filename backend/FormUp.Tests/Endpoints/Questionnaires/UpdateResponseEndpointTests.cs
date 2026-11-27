using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Questionnaires.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Questionnaires;

/// <summary>Integration tests for PUT /client/questionnaire/response/{id}.</summary>
[Collection(TestCollection.Name)]
public class UpdateResponseEndpointTests(FitnessApiFactory factory)
{
    private const string Choices = """{"options":["A","B"]}""";

    private static readonly SeedQuestion[] Form =
    [
        new("choice", QuestionTypes.SingleChoice, Choices),
        new("multi", QuestionTypes.MultiSelect, Choices),
        new("number", QuestionTypes.Number, """{"min":1,"max":10}"""),
        new("smiley", QuestionTypes.SmileyRating, """{"lowLabel":"low","highLabel":"high"}"""),
        new("scale", QuestionTypes.Scale),
        new("text", QuestionTypes.ShortText),
        new("section", QuestionTypes.Section),
        new("hidden", QuestionTypes.ShortText, Hidden: true),
        new("file", QuestionTypes.FileUpload),
        new("file2", QuestionTypes.FileUpload),
    ];

    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    private static object Answer(Guid questionId, string? text = null, decimal? number = null, string? json = null) =>
        new { questionPublicId = questionId, valueText = text, valueNumber = number, valueJson = json };

    private Task<HttpResponseMessage> PutAsync(QuestionnaireAnswerScenario scenario, params object[] answers) =>
        scenario.Client.Http.PutAsJsonAsync(
            $"/client/questionnaire/response/{scenario.ResponsePublicId}",
            new { answers },
            TestContext.Current.CancellationToken);

    private async Task AssertCodedBadRequestAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain(ErrorCodes.QuestionnaireAnswerInvalid);
    }

    [Fact]
    public async Task Put_ValidAnswers_Returns204AndPersists()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);

        var response = await PutAsync(
            scenario,
            Answer(scenario.QuestionIds["choice"], text: "A"),
            Answer(scenario.QuestionIds["multi"], json: """["A","B"]"""),
            Answer(scenario.QuestionIds["number"], number: 4.5m),
            Answer(scenario.QuestionIds["smiley"], number: 3),
            Answer(scenario.QuestionIds["scale"], number: 7),
            Answer(scenario.QuestionIds["text"], text: "hello"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await db.QuestionnaireResponses.Include(r => r.Answers)
            .FirstAsync(r => r.PublicId == scenario.ResponsePublicId, TestContext.Current.CancellationToken);
        saved.Status.Should().Be(QuestionnaireResponseStatus.InProgress);
        saved.Answers.Should().HaveCount(6);
    }

    [Fact]
    public async Task Put_ClearedAnswer_RemovesTheStoredAnswer()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);
        await PutAsync(scenario, Answer(scenario.QuestionIds["text"], text: "hello"));

        var response = await PutAsync(scenario, Answer(scenario.QuestionIds["text"], text: "  "));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await db.QuestionnaireResponses.Include(r => r.Answers)
            .FirstAsync(r => r.PublicId == scenario.ResponsePublicId, TestContext.Current.CancellationToken);
        saved.Answers.Should().BeEmpty();
    }

    [Theory]
    [InlineData("choice", "Z", null, null)]
    [InlineData("multi", null, null, """["A","A"]""")]
    [InlineData("multi", null, null, """["Z"]""")]
    [InlineData("multi", "Other", null, """["A"]""")]
    [InlineData("number", null, 11.0, null)]
    [InlineData("number", null, 0.0, null)]
    [InlineData("smiley", null, 6.0, null)]
    [InlineData("smiley", null, 2.5, null)]
    [InlineData("scale", null, 11.0, null)]
    [InlineData("text", "hello", 3.0, null)]
    public async Task Put_InvalidAnswerForType_Returns400WithCode(
        string key, string? text, double? number, string? json)
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);

        var response = await PutAsync(
            scenario,
            Answer(scenario.QuestionIds[key], text, number is null ? null : (decimal)number, json));

        await AssertCodedBadRequestAsync(response);
    }

    [Fact]
    public async Task Put_QuestionOfAnotherQuestionnaire_Returns400AndSavesNothing()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);
        var other = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);

        var response = await PutAsync(
            scenario,
            Answer(scenario.QuestionIds["text"], text: "valid"),
            Answer(other.QuestionIds["text"], text: "foreign"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(ErrorCodes.QuestionnaireQuestionNotInForm);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await db.QuestionnaireResponses.Include(r => r.Answers)
            .FirstAsync(r => r.PublicId == scenario.ResponsePublicId, TestContext.Current.CancellationToken);
        saved.Answers.Should().BeEmpty();
        saved.Status.Should().Be(QuestionnaireResponseStatus.Pending);
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("section")]
    [InlineData("unknown")]
    public async Task Put_UnknownHiddenOrSectionQuestion_Returns400(string key)
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);
        var questionId = key == "unknown" ? Guid.NewGuid() : scenario.QuestionIds[key];

        var response = await PutAsync(scenario, Answer(questionId, text: "x"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(ErrorCodes.QuestionnaireQuestionNotInForm);
    }

    [Theory]
    [MemberData(nameof(OutOfScopeLinks))]
    public async Task Put_ResponseOutsideTheCurrentCollaboration_Returns404(bool linkActive, bool activatedInFuture)
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(
            factory,
            Form,
            activatedAtUtc: activatedInFuture ? DateTime.UtcNow.AddHours(1) : null,
            linkActive: linkActive);

        var response = await PutAsync(scenario, Answer(scenario.QuestionIds["text"], text: "x"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    public static TheoryData<bool, bool> OutOfScopeLinks => new()
    {
        { false, false }, // inactive link
        { true, true }, // response predates the link's ActivatedAtUtc
    };

    [Fact]
    public async Task Put_OnSubmittedResponse_StillReturns409()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(
            factory, Form, status: QuestionnaireResponseStatus.Submitted);

        var response = await PutAsync(scenario, Answer(scenario.QuestionIds["text"], text: "x"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private FakeBlobStorageService Blobs => (FakeBlobStorageService)factory.Services.GetRequiredService<IBlobStorageService>();

    private static object FileAnswer(Guid questionId, Guid? uploadId, string? text = null) =>
        new { questionPublicId = questionId, fileUploadId = uploadId, valueText = text };

    /// <summary>Asks for an upload URL as the client, then seeds the staged bytes the PUT to that URL would have written.</summary>
    private async Task<Guid> StageAsync(QuestionnaireAnswerScenario scenario, byte[] data, string key = "file")
    {
        var ct = TestContext.Current.CancellationToken;
        var urlResponse = await scenario.Client.Http.PostAsJsonAsync(
            $"/client/questionnaire/response/{scenario.ResponsePublicId}/file-upload-url",
            new { questionPublicId = scenario.QuestionIds[key], contentType = "image/jpeg", sizeBytes = data.Length },
            ct);
        urlResponse.EnsureSuccessStatusCode();
        var uploadId = (await urlResponse.Content.ReadFromJsonAsync<UploadIdBody>(ct))!.UploadId;

        Blobs.SeedObject(
            QuestionnaireFilePolicy.BuildStagingContainerPath(scenario.ResponsePublicId, scenario.Client.UserId, uploadId), data);
        return uploadId;
    }

    private sealed record UploadIdBody(Guid UploadId);

    private async Task<string?> StoredFileUrlAsync(QuestionnaireAnswerScenario scenario, string key)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var questionPublicId = scenario.QuestionIds[key];
        return await db.QuestionnaireAnswers
            .Where(a => a.ResponseId == scenario.ResponseId && a.Question.PublicId == questionPublicId)
            .Select(a => a.FileUrl)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> AnswerCountAsync(QuestionnaireAnswerScenario scenario)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.QuestionnaireAnswers.CountAsync(
            a => a.ResponseId == scenario.ResponseId, TestContext.Current.CancellationToken);
    }

    private async Task AssertFileUploadNotFoundAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(ErrorCodes.QuestionnaireFileUploadNotFound);
    }

    [Fact]
    public async Task Put_StagedUpload_PromotesToTheFinalPathAndStoresOwnCanonicalUrl()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);
        var uploadId = await StageAsync(scenario, JpegBytes);

        var response = await PutAsync(scenario, FileAnswer(scenario.QuestionIds["file"], uploadId));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var finalPath = QuestionnaireFilePolicy.BuildFinalContainerPath(scenario.ResponsePublicId, uploadId, "jpg");
        var stagingPath = QuestionnaireFilePolicy.BuildStagingContainerPath(
            scenario.ResponsePublicId, scenario.Client.UserId, uploadId);
        (await StoredFileUrlAsync(scenario, "file")).Should().Be(finalPath);
        Blobs.UploadedPaths.Should().Contain(finalPath);
        Blobs.DeletedPaths.Should().Contain(stagingPath);
        (await Blobs.ObjectExistsAsync(stagingPath, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task Put_UploadIdStagedByAnotherClient_Returns400AndWritesNothing()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);
        var stranger = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var uploadId = Guid.NewGuid();
        Blobs.SeedObject(
            QuestionnaireFilePolicy.BuildStagingContainerPath(scenario.ResponsePublicId, stranger.UserId, uploadId), JpegBytes);

        var response = await PutAsync(scenario, FileAnswer(scenario.QuestionIds["file"], uploadId));

        await AssertFileUploadNotFoundAsync(response);
        (await AnswerCountAsync(scenario)).Should().Be(0);
        Blobs.UploadedPaths.Should().NotContain(
            QuestionnaireFilePolicy.BuildFinalContainerPath(scenario.ResponsePublicId, uploadId, "jpg"));
    }

    [Fact]
    public async Task Put_UploadIdStagedForAnotherResponseOfTheSameClient_Returns400AndWritesNothing()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);
        var otherResponse = await QuestionnaireAnswerScenario.CreateAsync(factory, Form, existingClient: scenario.Client);
        var uploadId = await StageAsync(otherResponse, JpegBytes);

        var response = await PutAsync(scenario, FileAnswer(scenario.QuestionIds["file"], uploadId));

        await AssertFileUploadNotFoundAsync(response);
        (await AnswerCountAsync(scenario)).Should().Be(0);
    }

    [Fact]
    public async Task Put_SameUploadIdOnTwoAnswers_Returns400AndUploadsNothing()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);
        var uploadId = await StageAsync(scenario, JpegBytes);

        var response = await PutAsync(
            scenario,
            FileAnswer(scenario.QuestionIds["file"], uploadId),
            FileAnswer(scenario.QuestionIds["file2"], uploadId));

        await AssertFileUploadNotFoundAsync(response);
        Blobs.UploadedPaths.Should().NotContain(
            QuestionnaireFilePolicy.BuildFinalContainerPath(scenario.ResponsePublicId, uploadId, "jpg"));
        (await AnswerCountAsync(scenario)).Should().Be(0);
    }

    [Fact]
    public async Task Put_SameFileQuestionTwice_Returns400AndUploadsNothing()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);
        var first = await StageAsync(scenario, JpegBytes);
        var second = await StageAsync(scenario, JpegBytes);

        var response = await PutAsync(
            scenario,
            FileAnswer(scenario.QuestionIds["file"], first),
            FileAnswer(scenario.QuestionIds["file"], second));

        await AssertFileUploadNotFoundAsync(response);
        Blobs.UploadedPaths.Should().NotContain(
            QuestionnaireFilePolicy.BuildFinalContainerPath(scenario.ResponsePublicId, first, "jpg"));
    }

    [Fact]
    public async Task Put_UploadIdAlreadyPromotedForAnotherQuestion_Returns400()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);
        var uploadId = await StageAsync(scenario, JpegBytes);
        (await PutAsync(scenario, FileAnswer(scenario.QuestionIds["file"], uploadId)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var replay = await PutAsync(scenario, FileAnswer(scenario.QuestionIds["file2"], uploadId));

        await AssertFileUploadNotFoundAsync(replay);
        (await StoredFileUrlAsync(scenario, "file2")).Should().BeNull();
    }

    [Fact]
    public async Task Put_ResendOfTheStoredUploadId_IsANoOp()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);
        var uploadId = await StageAsync(scenario, JpegBytes);
        await PutAsync(scenario, FileAnswer(scenario.QuestionIds["file"], uploadId));
        var finalPath = QuestionnaireFilePolicy.BuildFinalContainerPath(scenario.ResponsePublicId, uploadId, "jpg");
        var deletesBefore = Blobs.DeletedPaths.Count(path => path == finalPath);

        var resend = await PutAsync(scenario, FileAnswer(scenario.QuestionIds["file"], uploadId));

        resend.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StoredFileUrlAsync(scenario, "file")).Should().Be(finalPath);
        Blobs.DeletedPaths.Count(path => path == finalPath).Should().Be(deletesBefore);
        (await Blobs.ObjectExistsAsync(finalPath, TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task Put_ReplacingTheFile_DeletesTheOldFinalObjectAfterSaving()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);
        var first = await StageAsync(scenario, JpegBytes);
        await PutAsync(scenario, FileAnswer(scenario.QuestionIds["file"], first));
        var second = await StageAsync(scenario, JpegBytes);

        var response = await PutAsync(scenario, FileAnswer(scenario.QuestionIds["file"], second));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StoredFileUrlAsync(scenario, "file")).Should().Be(
            QuestionnaireFilePolicy.BuildFinalContainerPath(scenario.ResponsePublicId, second, "jpg"));
        Blobs.DeletedPaths.Should().Contain(
            QuestionnaireFilePolicy.BuildFinalContainerPath(scenario.ResponsePublicId, first, "jpg"));
    }

    [Fact]
    public async Task Put_ClearingTheFileAnswer_DeletesTheOldFinalObject()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);
        var uploadId = await StageAsync(scenario, JpegBytes);
        await PutAsync(scenario, FileAnswer(scenario.QuestionIds["file"], uploadId));

        var response = await PutAsync(scenario, FileAnswer(scenario.QuestionIds["file"], null));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StoredFileUrlAsync(scenario, "file")).Should().BeNull();
        Blobs.DeletedPaths.Should().Contain(
            QuestionnaireFilePolicy.BuildFinalContainerPath(scenario.ResponsePublicId, uploadId, "jpg"));
    }

    [Fact]
    public async Task Put_ReplacingAForeignLegacyUrl_LeavesTheForeignObjectAlone()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);
        const string legacyUrl = "https://storage.example.test/legacy/medical.pdf";
        await scenario.AddFileAnswerAsync(factory, "file", legacyUrl);
        var uploadId = await StageAsync(scenario, JpegBytes);

        var response = await PutAsync(scenario, FileAnswer(scenario.QuestionIds["file"], uploadId));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Blobs.DeletedPaths.Should().NotContain(path => path.Contains("legacy/medical.pdf"));
    }

    [Fact]
    public async Task Put_StagedBytesAreNotAnImage_Returns400AndDeletesTheStagedObject()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);
        var uploadId = await StageAsync(scenario, "<html><body>not an image</body></html>"u8.ToArray());

        var response = await PutAsync(scenario, FileAnswer(scenario.QuestionIds["file"], uploadId));

        await AssertCodedBadRequestAsync(response);
        Blobs.DeletedPaths.Should().Contain(
            QuestionnaireFilePolicy.BuildStagingContainerPath(scenario.ResponsePublicId, scenario.Client.UserId, uploadId));
        (await AnswerCountAsync(scenario)).Should().Be(0);
    }

    [Fact]
    public async Task Put_StagedObjectOverTheSizeCap_Returns400AndDeletesTheStagedObject()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);
        var oversized = new byte[(int)ImageUploadService.MaxImageSizeBytes + 1];
        JpegBytes.CopyTo(oversized, 0);
        var uploadId = await StageAsync(scenario, [0xFF, 0xD8, 0xFF]);
        Blobs.SeedObject(
            QuestionnaireFilePolicy.BuildStagingContainerPath(scenario.ResponsePublicId, scenario.Client.UserId, uploadId),
            oversized);

        var response = await PutAsync(scenario, FileAnswer(scenario.QuestionIds["file"], uploadId));

        await AssertCodedBadRequestAsync(response);
        Blobs.DeletedPaths.Should().Contain(
            QuestionnaireFilePolicy.BuildStagingContainerPath(scenario.ResponsePublicId, scenario.Client.UserId, uploadId));
    }

    [Fact]
    public async Task Put_FileUploadIdAlongsideText_Returns400()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);
        var uploadId = await StageAsync(scenario, JpegBytes);

        var response = await PutAsync(scenario, FileAnswer(scenario.QuestionIds["file"], uploadId, text: "extra"));

        await AssertCodedBadRequestAsync(response);
    }
}
