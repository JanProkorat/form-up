using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Questionnaires.CreateResponse;

public class CreateResponseEndpoint(IApplicationDbContext db, IClientLinkAuthorizationService linkAuthorizationService)
    : Endpoint<CreateResponseRequest, CreateResponseResponse>
{
    public override void Configure()
    {
        Post("/client/questionnaire/response");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Create questionnaire response";
            s.Description = "Creates a new in-progress questionnaire response for the authenticated client.";
            s.Response<CreateResponseResponse>(StatusCodes.Status201Created, "Response created");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status404NotFound, "Unknown questionnaire, no client profile, or no active link to its owner");
        });
    }

    public override async Task HandleAsync(CreateResponseRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null) { await Send.UnauthorizedAsync(ct); return; }
        var userGuid = Guid.Parse(userId);

        // 1. Find the questionnaire by PublicId
        var questionnaire = await db.Questionnaires
            .FirstOrDefaultAsync(q => q.PublicId == req.QuestionnairePublicId, ct);

        if (questionnaire is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // 2. Find the client's link to the professional
        var clientProfile = await db.ClientProfiles
            .FirstOrDefaultAsync(cp => cp.UserId == userGuid, ct);

        if (clientProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var professionalProfile = await db.ProfessionalProfiles
            .FirstOrDefaultAsync(p => p.UserId == questionnaire.ProfessionalId, ct);

        if (professionalProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // The professional and client profiles are already confirmed to exist above, so a null
        // result here can only mean "no active link" — not "no professional/client profile". No
        // capability flag is required, matching the pre-migration IsActive-only presence check.
        var capabilities = await linkAuthorizationService.GetCapabilitiesByClientUserIdAsync(
            questionnaire.ProfessionalId, userGuid, ct);

        if (capabilities is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // The service exposes only the link's capability flags, never its own database Id — the
        // new response still needs that Id as an FK, so it is looked up separately here. The
        // capability lookup above confirmed an active link existed at that point, but it can be
        // deactivated between the two queries, so this must still handle a missing link rather
        // than assume one (FirstOrDefaultAsync, not FirstAsync) to preserve the endpoint's
        // pre-migration atomic behaviour of "link missing -> 404", never a 500.
        var linkId = await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(l => l.ClientProfileId == clientProfile.Id
                        && l.ProfessionalProfileId == professionalProfile.Id
                        && l.ConnectionState != ConnectionState.Ended)
            .Select(l => (long?)l.Id)
            .FirstOrDefaultAsync(ct);

        if (linkId is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // 3. Create the response
        var response = new QuestionnaireResponse
        {
            PublicId = Guid.NewGuid(),
            QuestionnaireId = questionnaire.Id,
            ClientId = userGuid,
            ProfessionalId = questionnaire.ProfessionalId,
            LinkId = linkId.Value,
            Status = QuestionnaireResponseStatus.InProgress,
            Kind = questionnaire.Type == QuestionnaireType.CheckIn
                ? QuestionnaireResponseKind.CheckIn
                : QuestionnaireResponseKind.Onboarding,
        };

        db.QuestionnaireResponses.Add(response);
        await db.SaveChangesAsync(ct);

        await Send.ResponseAsync(
            new CreateResponseResponse { ResponsePublicId = response.PublicId },
            StatusCodes.Status201Created,
            ct);
    }
}
