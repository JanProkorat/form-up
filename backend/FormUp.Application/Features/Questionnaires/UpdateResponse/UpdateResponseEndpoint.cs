using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Features.Questionnaires.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Questionnaires.UpdateResponse;

public class UpdateResponseEndpoint(IApplicationDbContext db, TimeProvider timeProvider)
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
                            "config min/max (default 1-10); file_upload = https fileUrl; section takes no answer.";
            s.Response(StatusCodes.Status204NoContent, "Answers saved");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,
                "An answer is invalid (QUESTIONNAIRE_ANSWER_INVALID) or a question is not part of the form (QUESTIONNAIRE_QUESTION_NOT_IN_FORM)");
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
                question.Type, question.Config, answerDto.ValueText, answerDto.ValueNumber, answerDto.ValueJson, answerDto.FileUrl);

            if (problem is not null)
            {
                this.ThrowErrorWithCode(ErrorCodes.QuestionnaireAnswerInvalid, problem);
                return;
            }
        }

        // Transition from Pending to InProgress on first answer save
        if (response.Status == QuestionnaireResponseStatus.Pending)
        {
            response.Status = QuestionnaireResponseStatus.InProgress;
        }

        // 4. Upsert answers; a blank answer clears the stored one
        var existingAnswers = response.Answers.ToDictionary(a => a.QuestionId);

        foreach (var answerDto in req.Answers)
        {
            var questionId = questionMap[answerDto.QuestionPublicId].Id;
            existingAnswers.TryGetValue(questionId, out var existingAnswer);

            if (AnswerValueChecker.IsBlank(answerDto.ValueText, answerDto.ValueNumber, answerDto.ValueJson, answerDto.FileUrl))
            {
                if (existingAnswer is not null)
                {
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
            existingAnswer.FileUrl = answerDto.FileUrl;
        }

        await db.SaveChangesAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
