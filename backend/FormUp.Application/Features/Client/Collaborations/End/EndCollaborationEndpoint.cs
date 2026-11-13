using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Client.Collaborations.End;

/// <summary>
/// Deactivates a client-professional link and retires the plans that professional authored for
/// this client. This is permanent.
/// </summary>
/// <param name="db">Relational context — owns the link being deactivated.</param>
/// <param name="notifier">Realtime notifier used to tell the professional the link ended.</param>
/// <param name="notificationService">Persisted-notification service.</param>
/// <param name="mongo">MongoDB context — the departing professional's plans live here.</param>
/// <param name="audit">Audit logging service.</param>
public class EndCollaborationEndpoint(
    IApplicationDbContext db,
    IRealtimeNotifier notifier,
    INotificationService notificationService,
    IMongoContext mongo,
    IAuditService audit) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/client/collaborations/{publicId}");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "End a collaboration";
            s.Description = "Permanently deactivates a client-professional link.";
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null) { await Send.UnauthorizedAsync(ct); return; }

        var userGuid = Guid.Parse(userId);
        var publicId = Route<Guid>("publicId");

        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.UserId == userGuid, ct);

        if (clientProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var link = await db.ClientProfessionalLinks
            .Include(l => l.ProfessionalProfile)
                .ThenInclude(pp => pp.User)
            .FirstOrDefaultAsync(l => l.PublicId == publicId
                                   && l.ClientProfileId == clientProfile.Id
                                   && l.ConnectionState != ConnectionState.Ended, ct);

        if (link is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Archive BEFORE deactivating the link, not after. The link lookup above requires
        // a live ConnectionState, so if the Mongo write failed after the link had already been committed as
        // inactive, the retry would 404 here and the plans would stay Active with nobody able to
        // reach them — exactly the half-state this archival exists to prevent, and unrepairable.
        //
        // This order is retryable instead: a failed Mongo write leaves the link active, so the
        // whole operation can simply be re-issued. It is not free of consequence though — if the
        // archival succeeds and the SaveChangesAsync below then fails, the client's plans are
        // Archived while the collaboration is still live, and every client-facing read filters on
        // Status == Active, so the client sees no plan until the request is retried. Retryable
        // beats unrepairable, which is why the order is this way round, but the failure is not
        // invisible to the client and should not be described as harmless.
        await CollaborationPlanArchiver.ArchiveAsync(
            mongo,
            link.ProfessionalProfile.UserId,
            userGuid,
            archiveNutrition: true,
            archiveTraining: true,
            DateTime.UtcNow,
            ct);

        // Ending the package and payments is idempotent, so it also sits before the flip: a retry after a
        // failure finds the link still active and the terms already closed.
        await ClientPaymentTermsCloser.CloseAsync(
            db, audit, link.ProfessionalProfileId, clientProfile.Id, userGuid, DateTime.UtcNow, ct);

        link.ConnectionState = ConnectionState.Ended;
        await db.SaveChangesAsync(ct);

        // Notify the professional
        var clientUser = await db.Users.FirstAsync(u => u.Id == userGuid, ct);
        var clientName = $"{clientUser.FirstName} {clientUser.LastName}";
        var profName = $"{link.ProfessionalProfile.User.FirstName} {link.ProfessionalProfile.User.LastName}";

        await notificationService.CreateAsync(
            link.ProfessionalProfile.UserId,
            NotificationType.General,
            new Dictionary<string, string> { ["clientName"] = clientName },
            ct: ct);

        await notifier.NotifyAsync(link.ProfessionalProfile.UserId, "collaborationended", new
        {
            LinkPublicId = link.PublicId,
            ClientName = clientName
        }, ct);

        await Send.NoContentAsync(ct);
    }
}
