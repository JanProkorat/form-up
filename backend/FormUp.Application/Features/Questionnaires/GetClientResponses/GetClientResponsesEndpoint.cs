using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Questionnaires.Dtos;
using FormUp.Application.Features.Questionnaires.GetClientResponse;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Questionnaires.GetClientResponses;

/// <summary>
/// Returns all questionnaire responses (submitted, pending, in-progress) for a
/// specific client, scoped to the requesting professional. Supports response
/// history when a coach sends multiple questionnaires over time.
/// </summary>
public class GetClientResponsesEndpoint(
    IApplicationDbContext db,
    IClientLinkAuthorizationService linkAuthorizationService,
    IBlobStorageService blobStorage)
    : EndpointWithoutRequest<GetClientResponsesResponse>
{
    public override void Configure()
    {
        Get("/trainer/clients/{clientPublicId}/questionnaire-responses");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Get all questionnaire responses for a client";
            s.Description = "Returns all questionnaire responses scoped to the requesting professional, ordered by most recent first.";
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null) { await Send.UnauthorizedAsync(ct); return; }
        var userGuid = Guid.Parse(userId);
        var clientPublicId = Route<Guid>("clientPublicId");

        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.PublicId == clientPublicId, ct);

        if (clientProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

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

        var responses = await db.QuestionnaireResponses
            .Include(r => r.Answers).ThenInclude(a => a.Question)
            .Include(r => r.Questionnaire).ThenInclude(q => q.Questions)
            .Where(r => r.ClientId == clientProfile.UserId
                     && r.ProfessionalId == professionalProfile.UserId)
            .OrderByDescending(r => r.SubmittedAt ?? r.DateCreated)
            .ToListAsync(ct);

        // Stored FileUrl values are permanent unsigned references; only signed URLs are returned.
        var signedFileUrls = new Dictionary<string, string>();

        foreach (var storedUrl in responses
                     .Where(r => r.Status == QuestionnaireResponseStatus.Submitted)
                     .SelectMany(r => r.Answers)
                     .Select(a => a.FileUrl)
                     .Where(url => !string.IsNullOrEmpty(url))
                     .Distinct())
        {
            signedFileUrls[storedUrl!] = await blobStorage.GenerateReadUrlAsync(storedUrl, ct) ?? string.Empty;
        }

        await Send.OkAsync(new GetClientResponsesResponse
        {
            Responses = responses.Select(r =>
            {
                // #713 — QuestionId → (SectionLabel, SectionOrder), same resolver as
                // the single-response endpoint so both surfaces stay consistent.
                var sectionsByQuestionId = QuestionSectionResolver.Resolve(r.Questionnaire.Questions);

                return new ClientResponseItem
                {
                    ResponsePublicId = r.PublicId,
                    QuestionnaireTitle = r.Questionnaire.Title,
                    Status = r.Status.ToString(),
                    SubmittedAt = r.SubmittedAt,
                    DateCreated = r.DateCreated,
                    Scope = r.Scope,
                    AnswerCount = r.Answers.Count,
                    Answers = r.Status == QuestionnaireResponseStatus.Submitted
                        ? r.Answers.Select(a =>
                        {
                            var (sectionLabel, sectionOrder) = sectionsByQuestionId.GetValueOrDefault(a.QuestionId);
                            return new ResponseAnswerDto
                            {
                                QuestionPublicId = a.Question.PublicId,
                                QuestionLabel = a.Question.Label,
                                QuestionType = a.Question.Type,
                                MappedField = a.Question.MappedField,
                                ValueText = a.ValueText,
                                ValueNumber = a.ValueNumber,
                                ValueJson = a.ValueJson,
                                FileUrl = string.IsNullOrEmpty(a.FileUrl) ? a.FileUrl : signedFileUrls[a.FileUrl],
                                SectionLabel = sectionLabel,
                                SectionOrder = sectionOrder,
                            };
                        }).ToList()
                        : [],
                };
            }).ToList(),
        }, ct);
    }
}
