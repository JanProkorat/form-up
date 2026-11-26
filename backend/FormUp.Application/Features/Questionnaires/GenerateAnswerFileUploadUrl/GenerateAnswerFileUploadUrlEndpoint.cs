using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Questionnaires.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Questionnaires.GenerateAnswerFileUploadUrl;

/// <summary>
/// Generates a pre-signed URL for the client to upload a file for one file_upload answer, staged
/// under <see cref="QuestionnaireFilePolicy.StagingPrefix"/>. <c>UpdateResponseEndpoint</c> later
/// rebuilds this container path from (response, caller, uploadId) — the client never submits a path.
/// </summary>
/// <param name="db">Relational database context.</param>
/// <param name="imageUpload">Image upload service — validates size, then issues the signed URL.</param>
/// <param name="timeProvider">Clock, for the check-in answer window.</param>
internal sealed class GenerateAnswerFileUploadUrlEndpoint(
    IApplicationDbContext db,
    IImageUploadService imageUpload,
    TimeProvider timeProvider)
    : Endpoint<GenerateAnswerFileUploadUrlRequest, GenerateAnswerFileUploadUrlResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/client/questionnaire/response/{ResponsePublicId}/file-upload-url");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Generate a questionnaire file upload URL";
            s.Description = "Returns a time-limited pre-signed URL for direct upload of a jpeg, png or webp file " +
                            "(up to 5 MiB) answering a file_upload question, together with the uploadId to send as " +
                            "fileUploadId in PUT /client/questionnaire/response/{ResponsePublicId}.";
            s.Response<GenerateAnswerFileUploadUrlResponse>(StatusCodes.Status200OK, "Upload URL and uploadId");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,
                "Invalid content type or size, or the question is not a visible file_upload question of the form");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The response belongs to another client");
            s.Response(StatusCodes.Status404NotFound, "Unknown response, or one outside the current collaboration");
            s.Response(StatusCodes.Status409Conflict, "The response has already been submitted, or it is a check-in past its answer window (CHECK_IN_ANSWER_WINDOW_CLOSED)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GenerateAnswerFileUploadUrlRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var callerUserId = Guid.Parse(userId);
        var response = await LoadOpenResponseOrRespondAsync(req.ResponsePublicId, callerUserId, ct);

        if (response is null)
        {
            return;
        }

        var question = await db.QuestionnaireQuestions
            .AsNoTracking()
            .Where(q => q.QuestionnaireId == response.QuestionnaireId
                        && !q.IsHidden
                        && q.Type != QuestionTypes.Section
                        && q.PublicId == req.QuestionPublicId)
            .Select(q => new { q.Type })
            .FirstOrDefaultAsync(ct);

        if (question is null)
        {
            this.ThrowErrorWithCode(
                ErrorCodes.QuestionnaireQuestionNotInForm, "Question is not part of this questionnaire.");
            return;
        }

        if (question.Type != QuestionTypes.FileUpload)
        {
            this.ThrowErrorWithCode(
                ErrorCodes.QuestionnaireAnswerInvalid, "Only a file_upload question takes an uploaded file.");
            return;
        }

        var uploadId = Guid.NewGuid();
        var result = await imageUpload.GenerateUploadUrlAsync(
            ImageUploadScope.QuestionnaireUpload,
            QuestionnaireFilePolicy.BuildStagingSubPath(req.ResponsePublicId, callerUserId, uploadId),
            req.ContentType,
            req.SizeBytes,
            ct);

        await Send.OkAsync(new GenerateAnswerFileUploadUrlResponse
        {
            UploadUrl = result.UploadUrl,
            UploadId = uploadId,
        }, ct);
    }

    /// <summary>
    /// Same response guards as <c>UpdateResponseEndpoint</c>: 404 unknown or other collaboration,
    /// 403 another client's, 409 submitted or past the check-in window. Null means a response was written.
    /// </summary>
    private async Task<QuestionnaireResponse?> LoadOpenResponseOrRespondAsync(
        Guid responsePublicId, Guid callerUserId, CancellationToken ct)
    {
        var response = await db.QuestionnaireResponses
            .AsNoTracking()
            .Include(r => r.Link)
            .FirstOrDefaultAsync(r => r.PublicId == responsePublicId, ct);

        if (response is null)
        {
            await Send.NotFoundAsync(ct);
            return null;
        }

        if (response.ClientId != callerUserId)
        {
            await Send.ForbiddenAsync(ct);
            return null;
        }

        var clientProfileId = await db.ClientProfiles
            .Where(cp => cp.UserId == callerUserId)
            .Select(cp => (long?)cp.Id)
            .FirstOrDefaultAsync(ct);

        // A response of an ended or earlier collaboration is indistinguishable from an unknown one.
        if (clientProfileId is null
            || response.Link.ClientProfileId != clientProfileId
            || response.Link.ConnectionState == ConnectionState.Ended
            || (response.Link.ActivatedAtUtc != null && response.DateCreated < response.Link.ActivatedAtUtc))
        {
            await Send.NotFoundAsync(ct);
            return null;
        }

        if (response.Status == QuestionnaireResponseStatus.Submitted)
        {
            await HttpContext.Response.SendAsync(
                new { Error = "Response has already been submitted." },
                409, cancellation: ct);
            return null;
        }

        if (await this.RespondIfCheckInWindowClosedAsync(response, timeProvider.GetUtcNow().UtcDateTime, ct))
        {
            return null;
        }

        return response;
    }
}
