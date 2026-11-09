using System.Text.Json;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.AdminCoaches.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.AdminCoaches.GetAdminCoach;

/// <summary>
/// Returns one coach for the admin drawer: plan card, client count against the cap, and the history of admin and
/// coach actions on the subscription and account. Failure detail and payment history are empty until a payment
/// provider exists.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="logger">Logger for unreadable audit values.</param>
internal sealed class GetAdminCoachEndpoint(
    IApplicationDbContext db, TimeProvider timeProvider, ILogger<GetAdminCoachEndpoint> logger)
    : Endpoint<GetAdminCoachRequest, GetAdminCoachResponse>
{
    private const int MaxHistoryEntries = 100;

    /// <summary>Audit actions that make up a coach's history. Rows are matched by the coach's user id as entity id.</summary>
    private static readonly string[] HistoryActions =
    [
        AdminCoachAuditActions.GivePlanFree,
        AdminCoachAuditActions.CancelRenewal,
        AdminCoachAuditActions.AssignSubscription,
        CoachAccountDisabler.SelfAuditAction,
        CoachAccountDisabler.AdminAuditAction,
        AdminCoachAuditActions.KeepAccount,
        AdminCoachAuditActions.AddRole,
        AdminCoachAuditActions.RemoveRole,
    ];

    /// <inheritdoc />
    public override void Configure()
    {
        Get("/admin/coaches/{CoachId}");
        Roles(AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "Get a coach";
            s.Description = "Returns the coach's plan card (plan, status, price, clients against the cap) and history. The failure " +
                            "detail is always null and payments are not in the history until a payment provider exists. Only counts " +
                            "are shown for clients: no client name, email or data, and no IP address or raw audit values.";
            s.Response<GetAdminCoachResponse>(StatusCodes.Status200OK, "The coach");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "Caller is not an Admin");
            s.Response(StatusCodes.Status404NotFound, "The coach is unknown (PROFESSIONAL_NOT_FOUND)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GetAdminCoachRequest req, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();

        var coach = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(profile => profile.UserId == req.CoachId)
            .Select(AdminCoachQueries.Project)
            .FirstOrDefaultAsync(ct);

        if (coach is null)
        {
            await this.SendProblemAsync(
                StatusCodes.Status404NotFound, ErrorCodes.ProfessionalNotFound, "Coach not found.", ct);
            return;
        }

        var status = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(profile => profile.Id == coach.ProfessionalProfileId)
            .Select(CoachSubscriptionStatusClassifier.ProjectStatus(now))
            .Select(row => row.Status)
            .FirstAsync(ct);
        var profileIds = new[] { coach.ProfessionalProfileId };
        var clientsUsed = (await AdminCoachQueries.CountClientsAsync(db, profileIds, ct)).GetValueOrDefault(coach.ProfessionalProfileId);
        var heldRoles = await AdminCoachQueries.LoadHeldRolesAsync(db, [coach.CoachId], ct);
        var free = await AdminCoachQueries.LoadFreePlanAsync(db, ct);
        var (plan, maxActiveClients) = AdminCoachQueries.ResolveEffectivePlan(coach, free, now);
        var paying = status is AdminCoachStatus.Paying or AdminCoachStatus.PaymentFailed or AdminCoachStatus.Cancelling;
        var givenFree = status == AdminCoachStatus.GivenFree;

        await Send.OkAsync(new GetAdminCoachResponse
        {
            CoachId = coach.CoachId,
            Name = $"{coach.FirstName} {coach.LastName}".Trim(),
            Email = coach.Email,
            Roles = AdminCoachQueries.ActiveRoles(coach, heldRoles.GetValueOrDefault(coach.CoachId)),
            JoinedAt = coach.JoinedAt,
            Plan = plan,
            Status = status,
            PriceMinorUnits = paying ? PricePerPeriod(coach) : null,
            Currency = paying ? coach.Currency : null,
            BillingInterval = paying ? coach.PlanBillingInterval : null,
            CurrentPeriodEndsAt = paying ? coach.CurrentPeriodEndsAt : null,
            GivenFreeAt = givenFree ? coach.GivenFreeAt : null,
            GivenFreeUntil = givenFree ? coach.FreeUntil : null,
            CancelAtPeriodEnd = coach.CancelAtPeriodEnd,
            FailureDetail = null,
            ClientsUsed = clientsUsed,
            ClientsMax = maxActiveClients,
            History = await LoadHistoryAsync(coach, ct),
        }, ct);
    }

    private static long? PricePerPeriod(AdminCoachRecord coach) =>
        coach.PriceMinorUnits is { } price
            ? price + (coach.PaysSecondRoleAddOn && !coach.PlanBothRolesIncluded ? coach.AddOnMinorUnits ?? 0 : 0)
            : null;

    private async Task<List<AdminCoachHistoryEntryDto>> LoadHistoryAsync(AdminCoachRecord coach, CancellationToken ct)
    {
        // The coach's user id as entity id, never the professional profile's public id: other rows (a client
        // accepting an invite) are filed under that one with the client as the acting user.
        var rows = await db.AuditLogs
            .AsNoTracking()
            .Where(log => log.EntityId == coach.CoachId && HistoryActions.Contains(log.Action))
            .OrderByDescending(log => log.DateCreated)
            .Take(MaxHistoryEntries)
            .Select(log => new { log.Id, log.Action, log.UserId, log.DateCreated, log.NewValues })
            .ToListAsync(ct);

        var adminIds = rows
            .Where(row => row.UserId is not null && row.UserId != coach.CoachId)
            .Select(row => row.UserId!.Value)
            .Distinct()
            .ToList();
        var adminNames = await db.Users
            .AsNoTracking()
            .Where(user => adminIds.Contains(user.Id))
            .Select(user => new { user.Id, user.FirstName, user.LastName })
            .ToDictionaryAsync(user => user.Id, user => $"{user.FirstName} {user.LastName}".Trim(), ct);

        var history = new List<AdminCoachHistoryEntryDto>();

        foreach (var row in rows)
        {
            var kind = KindOf(row.Action);

            if (kind is null)
            {
                continue;
            }

            var byCoach = row.UserId == coach.CoachId;
            var values = ParseValues(row.Id, row.NewValues);

            history.Add(new AdminCoachHistoryEntryDto
            {
                OccurredAt = row.DateCreated,
                Kind = kind.Value,
                ByCoach = byCoach,
                ActorName = !byCoach && row.UserId is { } actorId ? adminNames.GetValueOrDefault(actorId) : null,
                Reason = values.Text("reason"),
                PlanCode = values.Text("planCode"),
                Until = values.Date("freeUntil") ?? values.Date("activeUntil") ?? values.Date("periodEndsAt"),
                Role = values.Role(),
            });
        }

        history.Add(new AdminCoachHistoryEntryDto { OccurredAt = coach.JoinedAt, Kind = AdminCoachHistoryKind.Joined });

        return history.OrderByDescending(entry => entry.OccurredAt).ToList();
    }

    private static AdminCoachHistoryKind? KindOf(string action) => action switch
    {
        AdminCoachAuditActions.GivePlanFree => AdminCoachHistoryKind.GivenFree,
        AdminCoachAuditActions.AssignSubscription => AdminCoachHistoryKind.PlanAssigned,
        AdminCoachAuditActions.CancelRenewal => AdminCoachHistoryKind.RenewalCancelled,
        CoachAccountDisabler.SelfAuditAction or CoachAccountDisabler.AdminAuditAction => AdminCoachHistoryKind.AccountDisabled,
        AdminCoachAuditActions.KeepAccount => AdminCoachHistoryKind.AccountKept,
        AdminCoachAuditActions.AddRole => AdminCoachHistoryKind.RoleAdded,
        AdminCoachAuditActions.RemoveRole => AdminCoachHistoryKind.RoleRemoved,
        _ => null,
    };

    private AuditValues ParseValues(long auditLogId, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AuditValues(null);
        }

        try
        {
            return new AuditValues(JsonDocument.Parse(json).RootElement.Clone());
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Audit row {AuditLogId} has values that are not valid JSON", auditLogId);
            return new AuditValues(null);
        }
    }

    private readonly record struct AuditValues(JsonElement? Root)
    {
        public string? Text(string name) =>
            Root is { ValueKind: JsonValueKind.Object } root
            && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        public DateTimeOffset? Date(string name) =>
            Root is { ValueKind: JsonValueKind.Object } root
            && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.TryGetDateTimeOffset(out var date)
                ? date
                : null;

        public string? Role()
        {
            var role = Text("addedRole") ?? Text("removedRole");

            return role is AppRoles.Trainer or AppRoles.Nutritionist or AppRoles.Client ? role : null;
        }
    }
}
