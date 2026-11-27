using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Questionnaires.GetTrainerQuestionnaire;

public class GetTrainerQuestionnairesResponse
{
    public List<QuestionnaireSummaryDto> Questionnaires { get; set; } = [];
}

public class QuestionnaireSummaryDto
{
    public Guid PublicId { get; set; }
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public QuestionnaireType Type { get; set; }
    public bool IsActive { get; set; }
    public bool IsDefault { get; set; }
    public int QuestionCount { get; set; }

    /// <summary>Distinct clients with a non-cancelled response to this form.</summary>
    public int UsedForClientCount { get; set; }

    /// <summary>
    /// Share of last week's check-ins that clients submitted, as a whole percent. Null for a form that is
    /// not a check-in or has no check-ins for last week.
    /// </summary>
    public int? LastWeekAnswerRatePercent { get; set; }
    public DateTime DateCreated { get; set; }

    /// <summary>Last edit time: the update timestamp, or the creation time when never edited.</summary>
    public DateTime EditedAt { get; set; }
}

public class GetTrainerQuestionnairesEndpoint(IApplicationDbContext db, TimeProvider timeProvider)
    : Endpoint<GetTrainerQuestionnairesRequest, GetTrainerQuestionnairesResponse>
{
    public override void Configure()
    {
        Get("/trainer/questionnaires");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "List trainer questionnaires";
            s.Description = "Returns the authenticated professional's questionnaire templates (without questions), optionally filtered by type and title search.";
            s.Responses[StatusCodes.Status200OK] = "Questionnaire summaries";
            s.Responses[StatusCodes.Status400BadRequest] = "Invalid type filter";
        });
    }

    public override async Task HandleAsync(GetTrainerQuestionnairesRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var userGuid = Guid.Parse(userId);

        var query = db.Questionnaires
            .AsNoTracking()
            .Where(q => q.ProfessionalId == userGuid);

        if (req.Type.HasValue)
        {
            query = query.Where(q => q.Type == req.Type.Value);
        }

        if (!string.IsNullOrWhiteSpace(req.Search))
        {
            var pattern = "%" + EscapeLikePattern(req.Search.Trim()) + "%";
            query = query.Where(q => EF.Functions.ILike(q.Title, pattern, "\\"));
        }

        var rows = await query
            .OrderByDescending(q => q.DateCreated)
            .Select(q => new QuestionnaireSummaryDto
            {
                PublicId = q.PublicId,
                Title = q.Title,
                Description = q.Description,
                Type = q.Type,
                IsActive = q.IsActive,
                IsDefault = q.IsDefault,
                QuestionCount = q.Questions.Count,
                DateCreated = q.DateCreated,
                EditedAt = q.DateUpdated ?? q.DateCreated,
            })
            .ToListAsync(ct);

        if (rows.Count > 0)
        {
            var publicIds = rows.Select(row => row.PublicId).ToList();

            var usedCounts = await db.QuestionnaireResponses
                .AsNoTracking()
                .Where(r => publicIds.Contains(r.Questionnaire.PublicId)
                    && r.Status != QuestionnaireResponseStatus.Cancelled)
                .GroupBy(r => r.Questionnaire.PublicId)
                .Select(g => new { PublicId = g.Key, Clients = g.Select(r => r.ClientId).Distinct().Count() })
                .ToDictionaryAsync(x => x.PublicId, x => x.Clients, ct);

            var rates = new Dictionary<Guid, int>();

            if (rows.Any(row => row.Type == QuestionnaireType.CheckIn))
            {
                var timeZoneId = await db.Users
                    .AsNoTracking()
                    .Where(u => u.Id == userGuid)
                    .Select(u => u.TimeZone)
                    .FirstOrDefaultAsync(ct);

                var localToday = ClientLocalDateResolver.ResolveLocalDate(
                    timeProvider.GetUtcNow().UtcDateTime,
                    ClientLocalDateResolver.ResolveTimeZone(timeZoneId));
                var lastWeekMonday = AutomationScheduleCalculator.MondayOf(localToday).AddDays(-7);

                var weekCounts = await db.QuestionnaireResponses
                    .AsNoTracking()
                    .AllCheckIns()
                    .Where(r => publicIds.Contains(r.Questionnaire.PublicId)
                        && r.CheckInWeekStart == lastWeekMonday)
                    .GroupBy(r => r.Questionnaire.PublicId)
                    .Select(g => new
                    {
                        PublicId = g.Key,
                        Total = g.Count(),
                        Submitted = g.Count(r => r.Status == QuestionnaireResponseStatus.Submitted),
                    })
                    .ToListAsync(ct);

                foreach (var count in weekCounts)
                {
                    rates[count.PublicId] = (int)Math.Round(
                        100.0 * count.Submitted / count.Total, MidpointRounding.AwayFromZero);
                }
            }

            foreach (var row in rows)
            {
                row.UsedForClientCount = usedCounts.GetValueOrDefault(row.PublicId);
                row.LastWeekAnswerRatePercent = row.Type == QuestionnaireType.CheckIn && rates.TryGetValue(row.PublicId, out var rate)
                    ? rate
                    : null;
            }
        }

        await Send.OkAsync(new GetTrainerQuestionnairesResponse { Questionnaires = rows }, ct);
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
