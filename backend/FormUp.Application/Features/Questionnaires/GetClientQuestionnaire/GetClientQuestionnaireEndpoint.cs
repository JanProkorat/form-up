using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Questionnaires.GetClientQuestionnaire;

public class GetClientQuestionnaireEndpoint(IApplicationDbContext db, TimeProvider timeProvider)
    : Endpoint<GetClientQuestionnaireRequest, GetClientQuestionnaireResponse>
{
    public override void Configure()
    {
        Get("/client/questionnaire");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Get client questionnaire";
            s.Description = "Returns the active questionnaire for a specific professional link. " +
                            "Pass ?linkPublicId= to select which coach's questionnaire to load. " +
                            "If omitted, falls back to the first active link (legacy behaviour). " +
                            "Pass ?responsePublicId= to load the questionnaire of one open response instead; " +
                            "an unknown, foreign, closed or inactive-link response is a 404.";
            s.Response<GetClientQuestionnaireResponse>(StatusCodes.Status200OK, "The questionnaire with any existing answers");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status404NotFound, "No matching questionnaire, response or active link");
            s.Response(StatusCodes.Status409Conflict, "The response is a check-in past its answer window (CHECK_IN_ANSWER_WINDOW_CLOSED)");
        });
    }

    public override async Task HandleAsync(GetClientQuestionnaireRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null) { await Send.UnauthorizedAsync(ct); return; }
        var userGuid = Guid.Parse(userId);

        // 1. Get the client's profile
        var clientProfile = await db.ClientProfiles
            .FirstOrDefaultAsync(cp => cp.UserId == userGuid, ct);

        if (clientProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // 2. Resolve the professional link — a named response wins, then explicit linkPublicId, then first active
        var linkPublicIdParam = req.LinkPublicId;
        var responsePublicIdParam = req.ResponsePublicId;

        Domain.Entities.QuestionnaireResponse? requestedResponse = null;
        Domain.Entities.ClientProfessionalLink? link;

        if (responsePublicIdParam.HasValue)
        {
            // Own response, still open, on a live link — anything else is indistinguishable from unknown.
            requestedResponse = await db.QuestionnaireResponses
                .Include(r => r.Answers)
                .Include(r => r.Link)
                .FirstOrDefaultAsync(r =>
                    r.PublicId == responsePublicIdParam.Value
                    && r.ClientId == userGuid
                    && r.Link.ClientProfileId == clientProfile.Id
                    && r.Link.ConnectionState != ConnectionState.Ended
                    && (r.Status == QuestionnaireResponseStatus.Pending || r.Status == QuestionnaireResponseStatus.InProgress), ct);

            link = requestedResponse?.Link;

            if (requestedResponse is not null
                && await this.RespondIfCheckInWindowClosedAsync(requestedResponse, timeProvider.GetUtcNow().UtcDateTime, ct))
            {
                return;
            }
        }
        else if (linkPublicIdParam.HasValue)
        {
            link = await db.ClientProfessionalLinks
                .Where(l => l.ClientProfileId == clientProfile.Id
                         && l.PublicId == linkPublicIdParam.Value
                         && l.ConnectionState != ConnectionState.Ended)
                .FirstOrDefaultAsync(ct);
        }
        else
        {
            // Legacy fallback: first active link
            link = await db.ClientProfessionalLinks
                .Where(l => l.ClientProfileId == clientProfile.Id && l.ConnectionState != ConnectionState.Ended)
                .FirstOrDefaultAsync(ct);
        }

        if (link is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // 3. Get the professional's profile
        var professionalProfile = await db.ProfessionalProfiles
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.Id == link.ProfessionalProfileId, ct);

        if (professionalProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // 4. Resolve questionnaire: link override → professional default
        Domain.Entities.Questionnaire? questionnaire = null;
        if (requestedResponse is not null)
        {
            questionnaire = await db.Questionnaires
                .Include(q => q.Questions
                    .Where(qq => !qq.IsHidden)
                    .OrderBy(qq => qq.OrderIndex))
                .FirstOrDefaultAsync(q => q.Id == requestedResponse.QuestionnaireId, ct);
        }
        else if (link.QuestionnaireId.HasValue)
        {
            questionnaire = await db.Questionnaires
                .Include(q => q.Questions
                    .Where(qq => !qq.IsHidden)
                    .OrderBy(qq => qq.OrderIndex))
                .FirstOrDefaultAsync(q => q.Id == link.QuestionnaireId.Value && q.IsActive, ct);
        }

        // A link whose coach chose "no onboarding form" never falls back to the default form.
        if (requestedResponse is null && !link.SkipOnboardingForm)
        {
            questionnaire ??= await db.Questionnaires
                .Include(q => q.Questions
                    .Where(qq => !qq.IsHidden)
                    .OrderBy(qq => qq.OrderIndex))
                .FirstOrDefaultAsync(q => q.ProfessionalId == professionalProfile.UserId && q.IsDefault && q.IsActive && q.Type == QuestionnaireType.Onboarding, ct);
        }

        if (questionnaire is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // 5. Check for an existing pending/in-progress response scoped to this link
        // Responses left over from an earlier collaboration on a re-activated link are ignored.
        var activatedAtUtc = link.ActivatedAtUtc;
        var existingResponse = requestedResponse ?? await db.QuestionnaireResponses
            .Include(r => r.Answers)
            .FirstOrDefaultAsync(r =>
                r.ClientId == userGuid
                && r.LinkId == link.Id
                && (activatedAtUtc == null || r.DateCreated >= activatedAtUtc)
                && r.QuestionnaireId == questionnaire.Id
                && (r.Status == QuestionnaireResponseStatus.Pending || r.Status == QuestionnaireResponseStatus.InProgress), ct);

        var questionLookup = questionnaire.Questions.ToDictionary(q => q.Id, q => q.PublicId);

        await Send.OkAsync(new GetClientQuestionnaireResponse
        {
            QuestionnairePublicId = questionnaire.PublicId,
            Title = questionnaire.Title,
            Description = questionnaire.Description,
            LinkPublicId = link.PublicId,
            ProfessionalName = $"{professionalProfile.User.FirstName} {professionalProfile.User.LastName}",
            ProfessionalRole = link.ProfessionalRole.ToString(),
            ProfessionalCity = professionalProfile.City,
            QuestionCount = questionnaire.Questions.Count,
            Questions = questionnaire.Questions.Select(qq => new ClientQuestionDto
            {
                PublicId = qq.PublicId,
                OrderIndex = qq.OrderIndex,
                Type = qq.Type,
                Label = qq.Label,
                HelperText = qq.HelperText,
                IsRequired = qq.IsRequired,
                Config = qq.Config,
            }).ToList(),
            ExistingResponsePublicId = existingResponse?.PublicId,
            ExistingResponseStatus = existingResponse?.Status.ToString(),
            ExistingAnswers = existingResponse?.Answers.Select(a => new ClientAnswerDto
            {
                QuestionPublicId = questionLookup.GetValueOrDefault(a.QuestionId),
                ValueText = a.ValueText,
                ValueNumber = a.ValueNumber,
                ValueJson = a.ValueJson,
                FileUrl = a.FileUrl,
            }).ToList(),
        }, ct);
    }
}
