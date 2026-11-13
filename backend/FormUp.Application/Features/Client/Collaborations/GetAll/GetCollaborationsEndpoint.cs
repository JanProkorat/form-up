using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Client.Collaborations.GetAll;

/// <summary>
/// Returns the client's active professional collaborations.
/// </summary>
public class GetCollaborationsEndpoint(
    IApplicationDbContext db, EntitlementService entitlementService, TimeProvider timeProvider)
    : EndpointWithoutRequest<GetCollaborationsResponse>
{
    public override void Configure()
    {
        Get("/client/collaborations");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Get active collaborations";
            s.Description = "Returns all active professional links for the authenticated client, each with the first payment of its current terms and the effective weekly check-in days.";
            s.Response<GetCollaborationsResponse>(StatusCodes.Status200OK, "The client's active collaborations");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null) { await Send.UnauthorizedAsync(ct); return; }

        var userGuid = Guid.Parse(userId);

        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.UserId == userGuid, ct);

        if (clientProfile is null)
        {
            await Send.OkAsync(new GetCollaborationsResponse(), ct);
            return;
        }

        var links = await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(l => l.ClientProfileId == clientProfile.Id && l.ConnectionState != ConnectionState.Ended)
            .Include(l => l.ProfessionalProfile)
                .ThenInclude(pp => pp.User)
            .Select(l => new CollaborationDto
            {
                PublicId = l.PublicId,
                ProfessionalPublicId = l.ProfessionalProfile.PublicId,
                ProfessionalName = l.ProfessionalProfile.User.FirstName + " " + l.ProfessionalProfile.User.LastName,
                ProfessionalCity = l.ProfessionalProfile.City,
                Role = l.ProfessionalRole.ToString(),
                CoveredType = CoachRoleStatus.ScopeFromGrant(l.CanViewNutritionPlans, l.CanViewTrainingPlans),
                Since = l.DateCreated,
                // Same fallback as SearchProfessionals / GetPublicProfile:
                // surface the personal user avatar when the pro-profile one
                // hasn't been set (most trainers upload only one photo today).
                AvatarBlobUrl = l.ProfessionalProfile.AvatarBlobUrl ?? l.ProfessionalProfile.User.AvatarBlobUrl
            })
            .OrderBy(l => l.Role)
            .ToListAsync(ct);

        await AttachFirstPaymentsAsync(links, clientProfile.Id, ct);
        await AttachCheckInsAsync(links, clientProfile.Id, ct);

        await Send.OkAsync(new GetCollaborationsResponse { Collaborations = links }, ct);
    }

    /// <summary>
    /// Sets each collaboration's first payment from the current terms of the client's assignment with that coach.
    /// </summary>
    private async Task AttachFirstPaymentsAsync(List<CollaborationDto> collaborations, long clientProfileId, CancellationToken ct)
    {
        var assignments = await db.ClientPaymentAssignments
            .AsNoTracking()
            .Where(a => a.ClientProfileId == clientProfileId)
            .Select(a => new { a.Id, ProfessionalPublicId = a.ProfessionalProfile.PublicId })
            .ToListAsync(ct);

        var today = ClientPaymentSchedule.Today(timeProvider.GetUtcNow().UtcDateTime);

        // A payment the coach asked for is the first payment, exactly as GET /client/payments lists it.
        var requested = await ClientPaymentQueries.RequestedOrdered(ClientPaymentQueries.VisibleToClient(db, clientProfileId).AsNoTracking())
            .ToListAsync(ct);
        var termsByAssignment = await ClientPaymentQueries.LoadTermsByAssignmentAsync(
            db, requested.Select(p => p.ClientPaymentAssignmentId).Distinct().ToList(), ct);

        // Payments of an earlier collaboration are not the first payment of the current one.
        requested = requested
            .Where(p => !ClientPaymentQueries.IsEarlierCollaboration(
                p, termsByAssignment.GetValueOrDefault(p.ClientPaymentAssignmentId) ?? []))
            .ToList();

        var requestedCoachIds = requested.Select(p => p.ProfessionalProfileId).Distinct().ToList();
        var coachPublicIds = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(c => requestedCoachIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.PublicId, ct);

        foreach (var collaboration in collaborations)
        {
            var open = requested.FirstOrDefault(p =>
                coachPublicIds.TryGetValue(p.ProfessionalProfileId, out var publicId) && publicId == collaboration.ProfessionalPublicId);

            if (open is null)
            {
                continue;
            }

            var openFigures = ClientPaymentSchedule.FiguresOf(open, [], []);

            collaboration.FirstPayment = new CollaborationFirstPaymentDto
            {
                PackageName = openFigures.PackageName,
                Amount = openFigures.Amount,
                Currency = open.Currency,
                Status = ClientPaymentSchedule.DerivedStatus(open.Status, open.DueDate, today),
                DueDate = open.DueDate,
                PaymentId = open.PublicId
            };
        }

        foreach (var assignment in assignments)
        {
            var collaboration = collaborations.FirstOrDefault(c => c.ProfessionalPublicId == assignment.ProfessionalPublicId);

            if (collaboration is null || collaboration.FirstPayment is not null)
            {
                continue;
            }

            var terms = await ClientPaymentSchedule.LoadTermsAsync(db, assignment.Id, ct);
            var current = terms.OrderByDescending(t => t.EffectiveFromUtc).ThenByDescending(t => t.Id).FirstOrDefault();

            if (current is null || current.EndsAtUtc is not null)
            {
                continue;
            }

            var payments = await db.ClientPayments
                .AsNoTracking()
                .Where(p => p.ClientPaymentAssignmentId == assignment.Id
                    && p.Kind == ClientPaymentKind.Recurring
                    && p.PeriodStart != null
                    && !p.IsKeptAfterEnd)
                .ToListAsync(ct);

            // Only the current collaboration counts: a returning client starts again.
            var first = payments
                .Where(p => !ClientPaymentQueries.IsEarlierCollaboration(p, terms))
                .OrderBy(p => p.PeriodStart)
                .FirstOrDefault();

            if (first is null)
            {
                continue;
            }

            var figures = ClientPaymentSchedule.FiguresOf(first, terms, ClientPaymentSchedule.RecurringStartsOf(payments));

            collaboration.FirstPayment = new CollaborationFirstPaymentDto
            {
                PackageName = figures.PackageName,
                Amount = figures.Amount,
                Currency = first.Currency,
                Status = ClientPaymentSchedule.DerivedStatus(first.Status, first.DueDate, today),
                DueDate = first.DueDate,
                PaymentId = first.Status is ClientPaymentStatus.Requested or ClientPaymentStatus.Paid ? first.PublicId : null
            };
        }
    }

    /// <summary>
    /// Sets each collaboration's check-in day from the coach's enabled check-in rule that covers the client,
    /// the way the automation worker resolves it: audience, then the client's override when the coach's
    /// subscription allows it.
    /// </summary>
    private async Task AttachCheckInsAsync(List<CollaborationDto> collaborations, long clientProfileId, CancellationToken ct)
    {
        var links = await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(l => l.ClientProfileId == clientProfileId && l.ConnectionState != ConnectionState.Ended)
            .Select(l => new { l.Id, l.PublicId, l.ProfessionalProfileId, l.ProfessionalRole })
            .ToListAsync(ct);

        if (links.Count == 0)
        {
            return;
        }

        var linkIds = links.Select(l => l.Id).ToList();

        var tagIdsByLink = (await db.ClientTagAssignments
                .AsNoTracking()
                .Where(a => linkIds.Contains(a.ClientProfessionalLinkId))
                .Select(a => new { a.ClientProfessionalLinkId, a.ClientTagId })
                .ToListAsync(ct))
            .ToLookup(a => a.ClientProfessionalLinkId, a => a.ClientTagId);

        var localToday = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        foreach (var link in links)
        {
            var entitlements = await entitlementService.GetEntitlementsAsync(link.ProfessionalProfileId, ct);

            if (!entitlements.CanUseWeeklyCheckIns)
            {
                continue;
            }

            var rule = await db.AutomationRules
                .AsNoTracking()
                .Include(r => r.Questionnaire)
                .Where(r => r.ActionType == AutomationActionType.SendForm
                            && r.Questionnaire!.Type == QuestionnaireType.CheckIn)
                .CoveringClient(link.ProfessionalProfileId, clientProfileId, tagIdsByLink[link.Id])
                .FirstOrDefaultAsync(ct);

            if (rule is null)
            {
                continue;
            }

            var clientOverride = await db.AutomationClientOverrides
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.AutomationRuleId == rule.Id && o.ClientProfileId == clientProfileId, ct);

            var effective = AutomationEffectiveRule.Resolve(
                rule, AutomationCheckInGate.ApplicableOverride(rule, clientOverride, entitlements), localToday);

            if (!effective.Enabled || effective.ScheduleWeekday is not { } weekday)
            {
                continue;
            }

            collaborations.First(c => c.PublicId == link.PublicId).CheckIns.Add(new CollaborationCheckInDto
            {
                Profession = CheckInResponses.ProfessionOf(link.ProfessionalRole),
                DayOfWeek = weekday
            });
        }
    }
}

public class GetCollaborationsResponse
{
    public List<CollaborationDto> Collaborations { get; set; } = [];
}

public class CollaborationDto
{
    public Guid PublicId { get; set; }
    public Guid ProfessionalPublicId { get; set; }
    public string ProfessionalName { get; set; } = string.Empty;
    public string? ProfessionalCity { get; set; }
    public string Role { get; set; } = string.Empty;

    /// <summary>Collaboration type the link covers, or null when it grants no plan view.</summary>
    public LinkCapabilityScope? CoveredType { get; set; }

    public DateTime Since { get; set; }
    /// <summary>
    /// Professional's avatar URL. Falls back to the underlying user's
    /// personal avatar when the professional-profile-specific one isn't set.
    /// Null when neither is uploaded.
    /// </summary>
    public string? AvatarBlobUrl { get; set; }

    /// <summary>
    /// First payment of the collaboration's current terms; null when the client has no terms with this professional.
    /// </summary>
    public CollaborationFirstPaymentDto? FirstPayment { get; set; }

    /// <summary>
    /// Weekdays the professional's weekly check-ins reach this client; empty when none is scheduled.
    /// </summary>
    public List<CollaborationCheckInDto> CheckIns { get; set; } = [];
}
