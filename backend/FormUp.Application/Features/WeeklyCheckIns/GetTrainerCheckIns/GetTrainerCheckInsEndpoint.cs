using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.WeeklyCheckIns.GetTrainerCheckIns;

/// <summary>
/// Returns check-ins for the caller's clients, either the active (week-agnostic) set or a
/// specific ISO week's check-ins. Only the current collaboration with each client counts.
/// </summary>
/// <param name="db">Relational database context.</param>
/// <param name="timeProvider">Clock, used to tell an expired check-in from a pending one.</param>
/// <param name="entitlementService">Resolves which live clients are over the cap of the plan in force.</param>
public class GetTrainerCheckInsEndpoint(
    IApplicationDbContext db, TimeProvider timeProvider, EntitlementService entitlementService)
    : Endpoint<GetTrainerCheckInsRequest, GetTrainerCheckInsResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/trainer/weekly-check-ins");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "List check-ins for the trainer";
            s.Description =
                "weekStartDate is optional (YYYY-MM-DD, must be a Monday). When omitted, " +
                "returns the active set for the authenticated trainer's clients — check-ins " +
                "the coach has not yet reviewed, across all weeks. When provided, returns the " +
                "check-ins of that specific week.";
            s.Responses[StatusCodes.Status200OK] = "Check-ins of the caller's clients";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or unreadable caller claim";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GetTrainerCheckInsRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var professionalUserId = Guid.Parse(userId);

        var query = db.QuestionnaireResponses
            .AsNoTracking()
            .CurrentCollaborationCheckIns()
            .Where(r => r.ProfessionalId == professionalUserId);

        query = req.WeekStartDate.HasValue
            ? query.Where(r => r.CheckInWeekStart == req.WeekStartDate.Value)
            : query.Where(r => r.ReviewedByCoachAtUtc == null);

        var rows = await query
            .OrderBy(r => r.Link.ClientProfile.User.LastName)
                .ThenBy(r => r.Link.ClientProfile.User.FirstName)
                .ThenBy(r => r.Id)
            .Select(r => new
            {
                r.PublicId,
                r.ClientId,
                ClientPublicId = r.Link.ClientProfile.PublicId,
                ClientName = r.Link.ClientProfile.User.FirstName + " " + r.Link.ClientProfile.User.LastName,
                r.Link.ProfessionalRole,
                r.CheckInWeekStart,
                r.DateCreated,
                r.Status,
                r.SubmittedAt,
                r.CheckInDueAtUtc,
                r.ReviewedByCoachAtUtc,
                r.Link.ClientProfileId,
                ClientTimeZone = r.Link.ClientProfile.User.TimeZone,
                FormId = r.Questionnaire.PublicId,
                FormName = r.Questionnaire.Title,
            })
            .ToListAsync(ct);

        var overCapClientProfileIds = await LoadOverCapClientProfileIdsAsync(professionalUserId, ct);

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        var checkIns = rows.Select(r => new TrainerCheckInDto
        {
            Id = r.PublicId,
            ClientUserId = r.ClientId,
            ClientPublicId = r.ClientPublicId,
            ClientName = r.ClientName,
            Profession = CheckInResponses.ProfessionOf(r.ProfessionalRole).ToString(),
            WeekStartDate = CheckInResponses.WeekStartOf(r.CheckInWeekStart, r.DateCreated),
            Flags = [],
            Note = null,
            SentAt = r.DateCreated,
            RespondedAt = r.SubmittedAt,
            DismissedByClientAt = null,
            ReviewedByTrainerAt = r.ReviewedByCoachAtUtc,
            Status = CheckInResponses.ResolveStatus(r.Status, r.ReviewedByCoachAtUtc, r.CheckInDueAtUtc, nowUtc).ToString(),
            DueAt = r.CheckInDueAtUtc,
            FormId = r.FormId,
            FormName = r.FormName,
            ClientTimeZone = r.ClientTimeZone,
            ClientIsOutsidePlan = overCapClientProfileIds.Contains(r.ClientProfileId),
        }).ToList();

        await Send.OkAsync(new GetTrainerCheckInsResponse { CheckIns = checkIns }, ct);
    }

    /// <summary>
    /// Client profile ids over the cap of the caller's plan, the same source as the clients list.
    /// Empty when the caller has no professional profile.
    /// </summary>
    private async Task<HashSet<long>> LoadOverCapClientProfileIdsAsync(Guid professionalUserId, CancellationToken ct)
    {
        var professionalProfileId = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(p => p.UserId == professionalUserId)
            .Select(p => (long?)p.Id)
            .FirstOrDefaultAsync(ct);

        if (professionalProfileId is null)
        {
            return [];
        }

        var state = await entitlementService.GetKeepClientsStateAsync(professionalProfileId.Value, ct);

        return state.OverCapClientProfileIds.ToHashSet();
    }
}
