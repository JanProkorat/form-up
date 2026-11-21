using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.WeeklyCheckIns.GetMyCheckInSchedule;

/// <summary>
/// Returns when the authenticated client's check-ins arrive, one entry per coach with an active rule.
/// </summary>
/// <param name="db">Relational database context (PostgreSQL via EF Core).</param>
/// <param name="entitlementService">Resolves the coach's subscription entitlements.</param>
/// <param name="timeProvider">Clock.</param>
public class GetMyCheckInScheduleEndpoint(
    IApplicationDbContext db, EntitlementService entitlementService, TimeProvider timeProvider)
    : EndpointWithoutRequest<GetMyCheckInScheduleResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/client/weekly-check-ins/schedule");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Get my check-in schedule (client)";
            s.Description =
                "Returns, per coach with an active check-in rule that reaches the caller, the weekday and " +
                "local time the check-in is sent and the weekday the answer window ends. Times are in " +
                "the caller's time zone. Disabled, paused or unentitled coaches are left out.";
            s.Response<GetMyCheckInScheduleResponse>(StatusCodes.Status200OK, "Check-in schedule, possibly empty");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
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

        var clientUserId = Guid.Parse(userId);

        var clientProfileId = await db.ClientProfiles
            .AsNoTracking()
            .Where(p => p.UserId == clientUserId)
            .Select(p => (long?)p.Id)
            .FirstOrDefaultAsync(ct);

        if (clientProfileId is null)
        {
            await Send.OkAsync(new GetMyCheckInScheduleResponse(), ct);
            return;
        }

        var ruleIds = await db.AutomationRules
            .AsNoTracking()
            .Where(r => r.ActionType == AutomationActionType.SendForm
                        && r.Questionnaire!.Type == QuestionnaireType.CheckIn
                        && (r.ClientProfileId == null || r.ClientProfileId == clientProfileId)
                        && db.ClientProfessionalLinks.Any(l =>
                            l.ProfessionalProfileId == r.OwnerProfessionalProfileId
                            && l.ClientProfileId == clientProfileId
                            && l.ConnectionState != ConnectionState.Ended))
            .Select(r => r.Id)
            .ToListAsync(ct);

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var entries = new List<(DateTime NextSendUtc, MyCheckInScheduleItem Item)>();

        foreach (var ruleId in ruleIds)
        {
            var evaluation = await AutomationEvaluationLoader.LoadAsync(
                db, entitlementService, ruleId, clientProfileId.Value, nowUtc, ct);

            if (evaluation is not { Effective: { ScheduleWeekday: { } weekday, LocalTime: { } localTime, AnswerWindowHours: { } windowHours } })
            {
                continue;
            }

            var nextSendUtc = AutomationNextSendCalculator.Compute(
                evaluation.Effective, evaluation.Zone, evaluation.StartLocalDate, [], nowUtc, evaluation.FirstSendNotBeforeUtc);

            if (nextSendUtc is null)
            {
                continue;
            }

            entries.Add((nextSendUtc.Value, new MyCheckInScheduleItem
            {
                ProfessionalPublicId = evaluation.Coach.PublicId,
                ProfessionalName = evaluation.CoachName,
                Profession = CheckInResponses.ProfessionOf(evaluation.Client.PrimaryLinkRole).ToString(),
                SendWeekday = weekday,
                SendTime = localTime.ToString("HH:mm"),
                AnswerUntilWeekday = TimeZoneInfo.ConvertTimeFromUtc(nextSendUtc.Value.AddHours(windowHours), evaluation.Zone).DayOfWeek,
            }));
        }

        // One entry per coach: when a coach has several rules, the one that sends first wins.
        var items = entries
            .OrderBy(e => e.NextSendUtc)
            .GroupBy(e => e.Item.ProfessionalPublicId)
            .Select(g => g.First().Item)
            .ToList();

        await Send.OkAsync(new GetMyCheckInScheduleResponse { Items = items }, ct);
    }
}
