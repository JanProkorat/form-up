using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Questionnaires.GetClientForms;

/// <summary>
/// Lists the forms the calling coach sent a linked client, with answers, split into the current
/// collaboration and earlier ones.
/// </summary>
/// <param name="db">Database context.</param>
/// <param name="linkAuthorizationService">Resolves the caller's live link to the client.</param>
/// <param name="timeProvider">Clock, used to tell a missed check-in from a waiting one.</param>
internal sealed class GetClientFormsEndpoint(
    IApplicationDbContext db,
    IClientLinkAuthorizationService linkAuthorizationService,
    TimeProvider timeProvider)
    : EndpointWithoutRequest<GetClientFormsResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/trainer/clients/{clientPublicId}/forms");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "List a client's forms and answers";
            s.Description = "Returns the onboarding, questionnaire and check-in forms the caller sent the client. " +
                "Unanswered forms are listed only for the current collaboration; answered forms from an earlier " +
                "collaboration come back in a separate group.";
            s.Responses[StatusCodes.Status200OK] = "Forms of the current and previous collaborations";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or unreadable caller claim";
            s.Responses[StatusCodes.Status404NotFound] = "Unknown client, or the caller has no live link to it";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var coachUserId = Guid.Parse(userId);
        var clientPublicId = Route<Guid>("clientPublicId");

        var capabilities = await linkAuthorizationService.GetCapabilitiesByClientPublicIdAsync(
            coachUserId, clientPublicId, ct);

        if (capabilities is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var clientUserId = await db.ClientProfiles
            .AsNoTracking()
            .Where(profile => profile.PublicId == clientPublicId)
            .Select(profile => (Guid?)profile.UserId)
            .FirstOrDefaultAsync(ct);

        if (clientUserId is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // The collaboration began when the live link was created or last reactivated.
        var activatedAtUtc = await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(link => link.ClientProfile.UserId == clientUserId
                           && link.ProfessionalProfile.UserId == coachUserId
                           && link.ConnectionState != ConnectionState.Ended)
            .MaxAsync(link => link.ActivatedAtUtc, ct);

        var responses = await db.QuestionnaireResponses
            .AsNoTracking()
            .Include(response => response.Answers).ThenInclude(answer => answer.Question)
            .Include(response => response.Questionnaire).ThenInclude(questionnaire => questionnaire.Questions)
            .Where(response => response.ClientId == clientUserId
                               && response.ProfessionalId == coachUserId
                               && response.Status != QuestionnaireResponseStatus.Cancelled)
            .OrderByDescending(response => response.SubmittedAt ?? response.DateCreated)
            .ToListAsync(ct);

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var result = new GetClientFormsResponse();

        foreach (var response in responses)
        {
            var isCurrent = CollaborationWindow.IsCurrent(
                activatedAtUtc, response.Status, response.SubmittedAt, response.DateCreated);

            if (isCurrent)
            {
                result.Current.Add(ClientFormItem.FromEntity(response, nowUtc));
            }
            else if (response.Status == QuestionnaireResponseStatus.Submitted)
            {
                result.Previous.Add(ClientFormItem.FromEntity(response, nowUtc));
            }
        }

        await Send.OkAsync(result, ct);
    }
}
