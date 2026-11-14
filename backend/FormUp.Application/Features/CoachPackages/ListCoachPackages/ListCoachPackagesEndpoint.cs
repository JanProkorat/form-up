using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.CoachPackages.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.CoachPackages.ListCoachPackages;

/// <summary>
/// Lists the calling coach's packages.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="entitlementService">Resolves subscription entitlements.</param>
internal sealed class ListCoachPackagesEndpoint(IApplicationDbContext db, EntitlementService entitlementService)
    : Endpoint<ListCoachPackagesRequest, ListCoachPackagesResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/payments/packages");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "List packages";
            s.Description = "Returns the calling coach's packages, active ones first. Archived ones only on request.";
            s.Responses[StatusCodes.Status200OK] = "The coach's packages";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or unreadable caller claim";
            s.Responses[StatusCodes.Status403Forbidden] = "The subscription does not include payments (PAYMENTS_NOT_ENTITLED)";
            s.Responses[StatusCodes.Status404NotFound] = "The caller has no professional profile";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(ListCoachPackagesRequest req, CancellationToken ct)
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

        var packages = await db.CoachPackages
            .AsNoTracking()
            .Where(p => p.ProfessionalProfileId == profileId && (req.IncludeArchived || p.ArchivedAt == null))
            .OrderBy(p => p.ArchivedAt != null)
            .ThenBy(p => p.Name)
            .ToListAsync(ct);

        var usage = await ClientPaymentSchedule.CountCurrentUsageAsync(db, profileId.Value, ct);

        await Send.OkAsync(new ListCoachPackagesResponse
        {
            Items = packages
                .Select(package => CoachPackageDto.FromEntity(package, usage.ByPackage.GetValueOrDefault(package.Id)))
                .ToList()
        }, ct);
    }
}
