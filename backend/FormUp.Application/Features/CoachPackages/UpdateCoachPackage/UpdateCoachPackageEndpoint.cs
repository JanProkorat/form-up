using System.Security.Claims;
using System.Text.Json;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.CoachPackages.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.CoachPackages.UpdateCoachPackage;

/// <summary>
/// Updates one of the calling coach's packages; a changed price is recorded as a new price-history row.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="entitlementService">Resolves subscription entitlements.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="timeProvider">Clock.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
internal sealed class UpdateCoachPackageEndpoint(
    IApplicationDbContext db,
    EntitlementService entitlementService,
    IAuditService audit,
    TimeProvider timeProvider)
    : Endpoint<UpdateCoachPackageRequest, CoachPackageDto>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/payments/packages/{PackageId}");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Update a package";
            s.Description = "Updates a package. A changed price applies to billing periods that start after the change.";
            s.Responses[StatusCodes.Status200OK] = "Updated package";
            s.Responses[StatusCodes.Status400BadRequest] = "Invalid request body";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or unreadable caller claim";
            s.Responses[StatusCodes.Status403Forbidden] = "The subscription does not include payments (PAYMENTS_NOT_ENTITLED), or the coach role was removed";
            s.Responses[StatusCodes.Status404NotFound] = "The package does not exist or belongs to another coach, or the caller has no professional profile";
            s.Responses[StatusCodes.Status409Conflict] = "The package is archived (PACKAGE_ARCHIVED), or its currency changes while client terms use it (CURRENCY_LOCKED)";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(UpdateCoachPackageRequest req, CancellationToken ct)
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

        if (package.ArchivedAt is not null)
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict, ErrorCodes.PackageArchived, "The package is archived.", ct);
            return;
        }

        var usage = await ClientPaymentSchedule.CountCurrentUsageAsync(db, profileId.Value, ct);

        if (package.Currency != req.Currency && usage.ByPackage.ContainsKey(package.Id))
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict,
                ErrorCodes.CurrencyLocked,
                "The currency cannot change while client terms use the package.",
                ct);
            return;
        }

        var oldPrice = package.Price;

        package.Name = req.Name.Trim();
        package.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
        package.Currency = req.Currency;
        package.Price = req.Price;
        package.Billing = req.Billing;
        package.IntervalWeeks = req.IntervalWeeks;
        package.RunsForPayments = req.RunsForPayments;
        package.PaymentDueDaysOverride = req.PaymentDueDaysOverride;
        package.ShowOnProfile = req.ShowOnProfile;

        var priceChanged = oldPrice != req.Price;

        if (priceChanged)
        {
            db.CoachPackagePrices.Add(new CoachPackagePrice
            {
                CoachPackageId = package.Id,
                Price = req.Price,
                EffectiveFromUtc = timeProvider.GetUtcNow().UtcDateTime
            });
        }

        await db.SaveChangesAsync(ct);

        if (priceChanged)
        {
            await audit.LogAsync(
                userId,
                "ChangeCoachPackagePrice",
                nameof(CoachPackage),
                package.PublicId,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                oldValues: JsonSerializer.Serialize(new { Price = oldPrice }),
                newValues: JsonSerializer.Serialize(new { package.Price }),
                ct: ct);
        }

        await Send.OkAsync(CoachPackageDto.FromEntity(package, usage.ByPackage.GetValueOrDefault(package.Id)), ct);
    }
}
