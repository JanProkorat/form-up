using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.ClientPayments.Shared;

/// <summary>
/// Load-and-authorize sequences shared by the coach-facing client payments endpoints.
/// </summary>
internal static class ClientPaymentGuards
{
    /// <summary>
    /// Proves the caller is a coach whose subscription includes payments and who has a live link to the
    /// client, and loads what the endpoints need. Writes the response and returns null otherwise: a coded
    /// 403 when the subscription lacks payments, a 404 when there is no profile, client or live link.
    /// </summary>
    /// <remarks>
    /// Payments belong to the coaching relationship, not to a plan domain, so a live link that grants
    /// neither nutrition nor training plans (<see cref="LinkCapabilities.GrantsNothing"/>) is enough.
    /// With <paramref name="allowEnded"/> a link that has ended is accepted too, but only when the coach and
    /// this exact client share a payment assignment: the kept periods stay the coach's to manage.
    /// </remarks>
    public static async Task<CoachClientContext?> LoadCoachClientContextOrRespondAsync(
        this IEndpoint endpoint,
        IApplicationDbContext db,
        EntitlementService entitlementService,
        IClientLinkAuthorizationService linkAuthorizationService,
        Guid userId,
        Guid clientPublicId,
        CancellationToken ct,
        bool allowEnded = false)
    {
        var profileId = await endpoint.LoadEntitledProfileIdOrRespondAsync(db, entitlementService, userId, ct);

        if (profileId is null)
        {
            return null;
        }

        var capabilities = await linkAuthorizationService.GetCapabilitiesByClientPublicIdAsync(userId, clientPublicId, ct);

        if (capabilities is null && !allowEnded)
        {
            await endpoint.HttpContext.Response.SendNotFoundAsync(ct);
            return null;
        }

        var client = await db.ClientProfiles
            .AsNoTracking()
            .Where(c => c.PublicId == clientPublicId)
            .Select(c => new
            {
                c.Id,
                c.UserId,
                c.User.FirstName,
                c.User.LastName,
                c.User.Email,
                c.User.Language
            })
            .FirstOrDefaultAsync(ct);

        var links = new List<(bool IsActive, DateTime DateCreated)>();

        if (client is not null)
        {
            links = (await db.ClientProfessionalLinks
                    .AsNoTracking()
                    .Where(l => l.ProfessionalProfileId == profileId && l.ClientProfileId == client.Id)
                    .Select(l => new { IsActive = l.ConnectionState != ConnectionState.Ended, l.DateCreated })
                    .ToListAsync(ct))
                .Select(l => (l.IsActive, l.DateCreated))
                .ToList();
        }

        // Without a live capability the link must have ended (never merely be unreadable) and a payment
        // assignment for exactly this coach and client must exist.
        var ended = capabilities is null;
        var linkDates = links.Where(l => ended ? !l.IsActive : l.IsActive).Select(l => l.DateCreated).ToList();

        if (client is null
            || linkDates.Count == 0
            || (ended && (links.Any(l => l.IsActive) || !await db.ClientPaymentAssignments
                .AnyAsync(a => a.ProfessionalProfileId == profileId && a.ClientProfileId == client.Id, ct))))
        {
            await endpoint.HttpContext.Response.SendNotFoundAsync(ct);
            return null;
        }

        var coach = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.FirstName, u.LastName })
            .FirstAsync(ct);

        return new CoachClientContext(
            profileId.Value,
            userId,
            $"{coach.FirstName} {coach.LastName}".Trim(),
            client.Id,
            client.UserId,
            client.FirstName,
            client.LastName,
            client.Email,
            client.Language,
            linkDates.Min(),
            ended);
    }

    /// <summary>
    /// Loads one of this client's payments for the caller, tracked and with its assignment. A payment that
    /// belongs to another coach, or to another client of the same coach, is a 404 like an unknown one.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="db">Application database context.</param>
    /// <param name="context">The proven coach and client.</param>
    /// <param name="paymentId">Public id of the payment.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<ClientPayment?> LoadPaymentOrRespondAsync(
        this IEndpoint endpoint,
        IApplicationDbContext db,
        CoachClientContext context,
        Guid paymentId,
        CancellationToken ct)
    {
        var payment = await db.ClientPayments
            .Include(p => p.ClientPaymentAssignment)
            .FirstOrDefaultAsync(
                p => p.PublicId == paymentId
                    && p.ProfessionalProfileId == context.ProfessionalProfileId
                    && p.ClientPaymentAssignment.ClientProfileId == context.ClientProfileId,
                ct);

        if (payment is null)
        {
            await endpoint.HttpContext.Response.SendNotFoundAsync(ct);
        }

        return payment;
    }

    /// <summary>
    /// Answers 409 when the payment is already paid or waived and returns true; returns false for a payment
    /// that can still change.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="payment">The payment.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<bool> RespondIfSettledAsync(this IEndpoint endpoint, ClientPayment payment, CancellationToken ct)
    {
        if (payment.Status == ClientPaymentStatus.Paid)
        {
            await endpoint.SendProblemAsync(StatusCodes.Status409Conflict, ErrorCodes.PaymentAlreadyPaid, "The payment is already paid.", ct);
            return true;
        }

        if (payment.Status == ClientPaymentStatus.Waived)
        {
            await endpoint.SendProblemAsync(StatusCodes.Status409Conflict, ErrorCodes.PaymentAlreadyWaived, "The payment is already waived.", ct);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Writes the figures an upcoming recurring payment currently shows onto the row, so they stop following
    /// the client's terms. Does nothing for a payment that is not upcoming or has no billing period.
    /// </summary>
    /// <param name="db">Application database context.</param>
    /// <param name="payment">The tracked payment.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task FreezeFiguresAsync(IApplicationDbContext db, ClientPayment payment, CancellationToken ct)
    {
        if (payment.Status != ClientPaymentStatus.Upcoming || payment.PeriodStart is null)
        {
            return;
        }

        var terms = await ClientPaymentSchedule.LoadTermsAsync(db, payment.ClientPaymentAssignmentId, ct);
        var recurringStarts = await ClientPaymentSchedule.LoadRecurringStartsAsync(db, payment.ClientPaymentAssignmentId, ct);

        ClientPaymentSchedule.Freeze(payment, terms, recurringStarts);
    }

    /// <summary>
    /// Loads the coach's payment relationship with the client, or null when none exists yet.
    /// </summary>
    /// <param name="db">Application database context.</param>
    /// <param name="context">The proven coach and client.</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task<ClientPaymentAssignment?> FindAssignmentAsync(
        IApplicationDbContext db, CoachClientContext context, CancellationToken ct) =>
        db.ClientPaymentAssignments
            .AsNoTracking()
            .FirstOrDefaultAsync(
                a => a.ProfessionalProfileId == context.ProfessionalProfileId && a.ClientProfileId == context.ClientProfileId,
                ct);

    /// <summary>
    /// Loads the coach's payment relationship with the client, creating it with the next free client
    /// number when this is the first. Must run in a transaction that holds the coach's profile lock, so
    /// two clients never get the same number.
    /// </summary>
    /// <param name="db">Application database context.</param>
    /// <param name="context">The proven coach and client.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The assignment, or null when the coach has used all 9999 client numbers.</returns>
    public static Task<ClientPaymentAssignment?> GetOrCreateAssignmentAsync(
        IApplicationDbContext db, CoachClientContext context, CancellationToken ct) =>
        ClientPaymentAssignments.GetOrCreateAsync(db, context.ProfessionalProfileId, context.ClientProfileId, ct);
}
