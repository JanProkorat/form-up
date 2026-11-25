using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Questionnaires.Dtos;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Questionnaires.GetClientResponse;

public class GetClientResponseEndpoint(
    IApplicationDbContext db,
    IClientLinkAuthorizationService linkAuthorizationService,
    IBlobStorageService blobStorage)
    : EndpointWithoutRequest<GetClientResponseResponse>
{
    public override void Configure()
    {
        Get("/trainer/clients/{clientPublicId}/questionnaire-response");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Get client questionnaire response";
            s.Description = "Returns the submitted questionnaire response for a client.";
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null) { await Send.UnauthorizedAsync(ct); return; }
        var userGuid = Guid.Parse(userId);
        var clientPublicId = Route<Guid>("clientPublicId");

        // Resolve ClientProfile.PublicId → UserId
        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.PublicId == clientPublicId, ct);

        if (clientProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Verify active link exists between this professional and client
        var professionalProfile = await db.ProfessionalProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(pp => pp.UserId == userGuid, ct);

        if (professionalProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // The professional and client profiles are already confirmed to exist above, so a null
        // result here can only mean "no active link" — not "no professional/client profile". No
        // capability flag is required, matching the pre-migration IsActive-only presence check.
        var capabilities = await linkAuthorizationService.GetCapabilitiesByClientPublicIdAsync(
            userGuid, clientPublicId, ct);

        if (capabilities is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var response = await db.QuestionnaireResponses
            .Include(r => r.Answers).ThenInclude(a => a.Question)
            .Include(r => r.Questionnaire).ThenInclude(q => q.Questions)
            .Where(r => r.ClientId == clientProfile.UserId
                     && r.ProfessionalId == professionalProfile.UserId
                     && r.Status == QuestionnaireResponseStatus.Submitted)
            .OrderByDescending(r => r.SubmittedAt)
            .FirstOrDefaultAsync(ct);

        if (response is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // #713 — QuestionId → (SectionLabel, SectionOrder) so the web can group
        // this flat answer list under the same section headers the questionnaire
        // builder shows, without a second fetch of the questionnaire definition.
        var sectionsByQuestionId = QuestionSectionResolver.Resolve(response.Questionnaire.Questions);

        var answers = new List<ResponseAnswerDto>();

        foreach (var a in response.Answers)
        {
            var (sectionLabel, sectionOrder) = sectionsByQuestionId.GetValueOrDefault(a.QuestionId);
            answers.Add(new ResponseAnswerDto
            {
                QuestionPublicId = a.Question.PublicId,
                QuestionLabel = a.Question.Label,
                QuestionType = a.Question.Type,
                MappedField = a.Question.MappedField,
                ValueText = a.ValueText,
                ValueNumber = a.ValueNumber,
                ValueJson = a.ValueJson,
                // Never the stored value: it is a permanent unsigned reference.
                FileUrl = string.IsNullOrEmpty(a.FileUrl)
                    ? a.FileUrl
                    : await blobStorage.GenerateReadUrlAsync(a.FileUrl, ct) ?? string.Empty,
                SectionLabel = sectionLabel,
                SectionOrder = sectionOrder,
            });
        }

        await Send.OkAsync(new GetClientResponseResponse
        {
            ResponsePublicId = response.PublicId,
            QuestionnaireTitle = response.Questionnaire.Title,
            SubmittedAt = response.SubmittedAt,
            AnswerCount = response.Answers.Count,
            Answers = answers,
        }, ct);
    }
}
