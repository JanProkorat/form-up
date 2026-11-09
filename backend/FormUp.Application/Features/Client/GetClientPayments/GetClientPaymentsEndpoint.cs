using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Client.GetClientPayments;

/// <summary>
/// Lists what the calling client owes their coaches and has paid. Only payments a coach has asked for are
/// shown, including those of coaches the client has since ended the collaboration with (flagged per coach);
/// the coach's subscription tier does not matter, so a payment already requested stays visible.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="timeProvider">Clock.</param>
internal sealed class GetClientPaymentsEndpoint(IApplicationDbContext db, TimeProvider timeProvider)
    : EndpointWithoutRequest<GetClientPaymentsResponse>
{
    private const int MaxPaidHistory = 100;

    /// <inheritdoc />
    public override void Configure()
    {
        Get("/client/payments");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "List my payments";
            s.Description = "Lists the payments the client's coaches asked for (overdue ones marked) and the latest paid ones, grouped by coach. Coaches whose collaboration ended stay listed, flagged collaborationEnded. Empty when the client has no payments.";
            s.Response<GetClientPaymentsResponse>(StatusCodes.Status200OK, "The client's payments");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(AppClaims.UserId), out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var clientProfileId = await db.ClientProfiles
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => (long?)c.Id)
            .FirstOrDefaultAsync(ct);

        if (clientProfileId is null)
        {
            await Send.OkAsync(new GetClientPaymentsResponse(), ct);
            return;
        }

        var visible = ClientPaymentQueries.VisibleToClient(db, clientProfileId.Value).AsNoTracking();

        var requested = await ClientPaymentQueries.RequestedOrdered(visible).ToListAsync(ct);
        var paid = await visible
            .Where(p => p.Status == ClientPaymentStatus.Paid)
            .OrderByDescending(p => p.PaidOn)
            .ThenByDescending(p => p.Id)
            .Take(MaxPaidHistory)
            .ToListAsync(ct);

        var coachIds = requested.Concat(paid).Select(p => p.ProfessionalProfileId).Distinct().ToList();
        var coaches = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(c => coachIds.Contains(c.Id))
            .Select(c => new { c.Id, c.PublicId, c.User.FirstName, c.User.LastName })
            .ToListAsync(ct);
        var activeCoachIds = (await db.ClientProfessionalLinks
                .AsNoTracking()
                .Where(l => l.ClientProfileId == clientProfileId && l.ConnectionState != ConnectionState.Ended && coachIds.Contains(l.ProfessionalProfileId))
                .Select(l => l.ProfessionalProfileId)
                .ToListAsync(ct))
            .ToHashSet();
        var today = ClientPaymentSchedule.Today(timeProvider.GetUtcNow().UtcDateTime);
        var termsByAssignment = await ClientPaymentQueries.LoadTermsByAssignmentAsync(
            db, requested.Concat(paid).Select(p => p.ClientPaymentAssignmentId).Distinct().ToList(), ct);

        bool IsEarlier(ClientPayment payment) =>
            ClientPaymentQueries.IsEarlierCollaboration(
                payment, termsByAssignment.GetValueOrDefault(payment.ClientPaymentAssignmentId) ?? []);

        await Send.OkAsync(
            new GetClientPaymentsResponse
            {
                Coaches = coaches
                    .OrderBy(c => c.FirstName).ThenBy(c => c.LastName)
                    .Select(coach => new ClientPaymentCoachDto
                    {
                        CoachId = coach.PublicId,
                        CoachName = $"{coach.FirstName} {coach.LastName}".Trim(),
                        CollaborationEnded = !activeCoachIds.Contains(coach.Id),
                        Requested = requested
                            .Where(p => p.ProfessionalProfileId == coach.Id)
                            .Select(p => ClientPaymentItemDto.FromEntity(p, today, IsEarlier(p)))
                            .ToList(),
                        Paid = paid
                            .Where(p => p.ProfessionalProfileId == coach.Id)
                            .Select(p => ClientPaymentItemDto.FromEntity(p, today, IsEarlier(p)))
                            .ToList()
                    })
                    .ToList()
            },
            ct);
    }
}
