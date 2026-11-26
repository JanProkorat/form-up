using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Questionnaires.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Questionnaires.UpdateResponse;

/// <summary>
/// Saves the answers of an in-progress questionnaire response; file_upload answers are promoted from
/// the caller's staged upload to a permanent object.
/// </summary>
/// <param name="db">Relational database context.</param>
/// <param name="timeProvider">Clock, for the check-in answer window.</param>
/// <param name="blobStorage">Blob storage — staged upload download, promotion and cleanup.</param>
public class UpdateResponseEndpoint(IApplicationDbContext db, TimeProvider timeProvider, IBlobStorageService blobStorage)
    : Endpoint<UpdateResponseRequest>
{
    public override void Configure()
    {
        Put("/client/questionnaire/response/{ResponsePublicId:guid}");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Update questionnaire response";
            s.Description = "Saves or updates answers for an in-progress questionnaire response. " +
                            "Each answer is validated against its question's type and config; an answer with every value blank " +
                            "clears the stored answer. Encoding: single_choice = valueText; multi_select = valueJson string array " +
                            "(+ valueText for 'Other' when config.allowOther); short_text = valueText; number = valueNumber within " +
                            "config min/max; smiley_rating / star_rating = whole valueNumber 1-5; scale = whole valueNumber within " +
                            "config min/max (default 1-10); file_upload = fileUploadId from POST .../file-upload-url (or the one already stored, a no-op resend); section takes no answer.";
            s.Response(StatusCodes.Status204NoContent, "Answers saved");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,
                "An answer is invalid (QUESTIONNAIRE_ANSWER_INVALID), a question is not part of the form (QUESTIONNAIRE_QUESTION_NOT_IN_FORM), or a fileUploadId is unknown or repeated (QUESTIONNAIRE_FILE_UPLOAD_NOT_FOUND)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The response belongs to another client");
            s.Response(StatusCodes.Status404NotFound, "Unknown response, or one outside the current collaboration");
            s.Response(StatusCodes.Status409Conflict, "The response has already been submitted, or it is a check-in past its answer window (CHECK_IN_ANSWER_WINDOW_CLOSED)");
        });
    }

    public override async Task HandleAsync(UpdateResponseRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null) { await Send.UnauthorizedAsync(ct); return; }
        var userGuid = Guid.Parse(userId);

        // 1. Find response and verify ownership + collaboration + status
        var response = await db.QuestionnaireResponses
            .Include(r => r.Answers)
            .Include(r => r.Link)
            .FirstOrDefaultAsync(r => r.PublicId == req.ResponsePublicId, ct);

        if (response is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (response.ClientId != userGuid)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var clientProfileId = await db.ClientProfiles
            .Where(cp => cp.UserId == userGuid)
            .Select(cp => (long?)cp.Id)
            .FirstOrDefaultAsync(ct);

        // A response of an ended or earlier collaboration is indistinguishable from an unknown one.
        if (clientProfileId is null
            || response.Link.ClientProfileId != clientProfileId
            || response.Link.ConnectionState == ConnectionState.Ended
            || (response.Link.ActivatedAtUtc != null && response.DateCreated < response.Link.ActivatedAtUtc))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (response.Status == QuestionnaireResponseStatus.Submitted)
        {
            await HttpContext.Response.SendAsync(
                new { Error = "Response has already been submitted." },
                409, cancellation: ct);
            return;
        }

        if (await this.RespondIfCheckInWindowClosedAsync(response, timeProvider.GetUtcNow().UtcDateTime, ct))
        {
            return;
        }

        // 2. Resolve question PublicIds, restricted to the visible, answerable questions of this response's questionnaire
        var questionPublicIds = req.Answers.Select(a => a.QuestionPublicId).ToList();
        var questionMap = await db.QuestionnaireQuestions
            .Where(q => q.QuestionnaireId == response.QuestionnaireId
                        && !q.IsHidden
                        && q.Type != QuestionTypes.Section
                        && questionPublicIds.Contains(q.PublicId))
            .ToDictionaryAsync(q => q.PublicId, ct);

        // 3. Validate every answer before anything is written
        var seenUploadIds = new HashSet<Guid>();
        var seenFileQuestions = new HashSet<Guid>();

        foreach (var answerDto in req.Answers)
        {
            if (!questionMap.TryGetValue(answerDto.QuestionPublicId, out var question))
            {
                this.ThrowErrorWithCode(
                    ErrorCodes.QuestionnaireQuestionNotInForm,
                    "Question is not part of this questionnaire.");
                return;
            }

            var problem = AnswerValueChecker.FindProblem(
                question.Type, question.Config, answerDto.ValueText, answerDto.ValueNumber, answerDto.ValueJson, answerDto.FileUploadId);

            if (problem is not null)
            {
                this.ThrowErrorWithCode(ErrorCodes.QuestionnaireAnswerInvalid, problem);
                return;
            }

            // One upload per answer: a shared id would promote one staged object to one shared final key.
            if (question.Type == QuestionTypes.FileUpload
                && (!seenFileQuestions.Add(answerDto.QuestionPublicId)
                    || (answerDto.FileUploadId.HasValue && !seenUploadIds.Add(answerDto.FileUploadId.Value))))
            {
                this.ThrowErrorWithCode(
                    ErrorCodes.QuestionnaireFileUploadNotFound,
                    "A file upload can back one answer only, and each file question may appear once.");
                return;
            }
        }

        var existingAnswers = response.Answers.ToDictionary(a => a.QuestionId);

        // 4. Download, size-check and sniff every staged upload before any object is written
        var promotions = await this.LoadStagedUploadsOrRespondAsync(
            req, questionMap, existingAnswers, userGuid, ct);

        if (promotions is null)
        {
            return;
        }

        // 5. Upload the final objects
        foreach (var promotion in promotions.Values)
        {
            await blobStorage.UploadAsync(promotion.FinalPath, promotion.Data, promotion.ContentType, ct);
        }

        // Transition from Pending to InProgress on first answer save
        if (response.Status == QuestionnaireResponseStatus.Pending)
        {
            response.Status = QuestionnaireResponseStatus.InProgress;
        }

        // 6. Upsert answers; a blank answer clears the stored one
        var replacedFileUrls = new List<string>();

        foreach (var answerDto in req.Answers)
        {
            var questionId = questionMap[answerDto.QuestionPublicId].Id;
            existingAnswers.TryGetValue(questionId, out var existingAnswer);

            if (AnswerValueChecker.IsBlankUpload(answerDto.ValueText, answerDto.ValueNumber, answerDto.ValueJson, answerDto.FileUploadId))
            {
                if (existingAnswer is not null)
                {
                    if (existingAnswer.FileUrl is not null)
                    {
                        replacedFileUrls.Add(existingAnswer.FileUrl);
                    }

                    db.QuestionnaireAnswers.Remove(existingAnswer);
                    existingAnswers.Remove(questionId);
                }

                continue;
            }

            if (existingAnswer is null)
            {
                existingAnswer = new QuestionnaireAnswer
                {
                    PublicId = Guid.NewGuid(),
                    ResponseId = response.Id,
                    QuestionId = questionId,
                };
                db.QuestionnaireAnswers.Add(existingAnswer);
                existingAnswers[questionId] = existingAnswer;
            }

            existingAnswer.ValueText = answerDto.ValueText;
            existingAnswer.ValueNumber = answerDto.ValueNumber;
            existingAnswer.ValueJson = answerDto.ValueJson;

            // A resend of the stored upload is not in promotions and keeps its URL.
            if (answerDto.FileUploadId is { } uploadId && promotions.TryGetValue(uploadId, out var promoted))
            {
                if (existingAnswer.FileUrl is not null)
                {
                    replacedFileUrls.Add(existingAnswer.FileUrl);
                }

                existingAnswer.FileUrl = blobStorage.BuildPublicUrl(promoted.FinalPath);
            }
            else if (answerDto.FileUploadId is null)
            {
                existingAnswer.FileUrl = null;
            }
        }

        await db.SaveChangesAsync(ct);

        // 7. Cleanup only after a successful save, so a failed save stays retryable
        foreach (var promotion in promotions.Values)
        {
            await blobStorage.DeleteAsync(promotion.StagingPath, ct);
        }

        foreach (var oldUrl in replacedFileUrls)
        {
            // Only our own canonical URLs are deleted; a foreign or legacy value is left alone.
            if (QuestionnaireFilePolicy.TryParseOwnFinalUrl(
                    blobStorage, response.PublicId, oldUrl, out var oldUploadId, out var oldExtension))
            {
                await blobStorage.DeleteAsync(
                    QuestionnaireFilePolicy.BuildFinalContainerPath(response.PublicId, oldUploadId, oldExtension), ct);
            }
        }

        await Send.NoContentAsync(ct);
    }

    /// <summary>
    /// Downloads and sniffs each staged upload the request references (keyed by uploadId), skipping a
    /// resend of the upload already stored on the same question. Responds 400 and returns null when
    /// one is missing, oversized or not a jpeg/png/webp image.
    /// </summary>
    private async Task<Dictionary<Guid, StagedUpload>?> LoadStagedUploadsOrRespondAsync(
        UpdateResponseRequest req,
        Dictionary<Guid, QuestionnaireQuestion> questionMap,
        Dictionary<long, QuestionnaireAnswer> existingAnswers,
        Guid callerUserId,
        CancellationToken ct)
    {
        var promotions = new Dictionary<Guid, StagedUpload>();

        foreach (var answerDto in req.Answers.Where(a => a.FileUploadId.HasValue))
        {
            var uploadId = answerDto.FileUploadId!.Value;
            existingAnswers.TryGetValue(questionMap[answerDto.QuestionPublicId].Id, out var existingAnswer);

            // No-op only against the stored answer of THIS question, never against the response prefix.
            if (existingAnswer is not null
                && QuestionnaireFilePolicy.IsOwnFinalUrlFor(blobStorage, req.ResponsePublicId, uploadId, existingAnswer.FileUrl))
            {
                continue;
            }

            var stagingPath = QuestionnaireFilePolicy.BuildStagingContainerPath(req.ResponsePublicId, callerUserId, uploadId);
            var staged = await blobStorage.DownloadAsync(stagingPath, ImageUploadService.MaxImageSizeBytes, ct);

            if (staged is null)
            {
                this.ThrowErrorWithCode(
                    ErrorCodes.QuestionnaireFileUploadNotFound, "No staged upload found for this fileUploadId.");
                return null;
            }

            var contentType = staged.Data is null ? null : ImageSignatureSniffer.SniffContentType(staged.Data);

            if (staged.Data is null || contentType is null)
            {
                await blobStorage.DeleteAsync(stagingPath, ct);
                this.ThrowErrorWithCode(
                    ErrorCodes.QuestionnaireAnswerInvalid,
                    staged.Data is null
                        ? "Uploaded file exceeds the maximum allowed size."
                        : "Uploaded file is not a recognized jpeg/png/webp image.");
                return null;
            }

            promotions[uploadId] = new StagedUpload(
                stagingPath,
                QuestionnaireFilePolicy.BuildFinalContainerPath(
                    req.ResponsePublicId, uploadId, ImageSignatureSniffer.ExtensionFor(contentType)),
                contentType,
                staged.Data);
        }

        return promotions;
    }

    private sealed record StagedUpload(string StagingPath, string FinalPath, string ContentType, byte[] Data);
}
