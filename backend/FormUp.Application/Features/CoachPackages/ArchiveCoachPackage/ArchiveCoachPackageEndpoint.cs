using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.CoachPackages.ArchiveCoachPackage;

/// <summary>
/// Archives one of the calling coach's packages. Archiving an archived package succeeds without change.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="entitlementService">Resolves subscription entitlements.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="timeProvider">Clock.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
internal sealed class ArchiveCoachPackageEndpoint(
    IApplicationDbContext db,
    EntitlementService entitlementService,
    IAuditService audit,
    TimeProvider timeProvider)
    : Endpoint<ArchiveCoachPackageRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/payments/packages/{PackageId}/archive");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Archive a package";
            s.Description = "Archives a package so it can no longer be edited or offered. Idempotent.";
            s.Responses[StatusCodes.Status204NoContent] = "Package archived";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or unreadable caller claim";
            s.Responses[StatusCodes.Status403Forbidden] = "The subscription does not include payments (PAYMENTS_NOT_ENTITLED), or the coach role was removed";
            s.Responses[StatusCodes.Status404NotFound] = "The package does not exist or belongs to another coach, or the caller has no professional profile";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(ArchiveCoachPackageRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(AppClaims.UserId), out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var profileId = await this.LoadPackagesEntitledProfileIdOrRespondAsync(db, entitlementService, userId, ct);

        if (profileId is null)
        {
            return;
        }

        var package = await db.CoachPackages
            .FirstOrDefaultAsync(p => p.PublicId == req.PackageId && p.ProfessionalProfileId == profileId, ct);

        if (package is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (package.ArchivedAt is null)
        {
            package.ArchivedAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(
                userId,
                "ArchiveCoachPackage",
                nameof(CoachPackage),
                package.PublicId,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                ct: ct);
        }

        await Send.NoContentAsync(ct);
    }
}
