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
using FormUp.Application.Features.Discounts.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Discounts.UpdateCoachDiscount;

/// <summary>
/// Updates one of the calling coach's discounts.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="entitlementService">Resolves subscription entitlements.</param>
/// <param name="audit">Audit logging service.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
internal sealed class UpdateCoachDiscountEndpoint(
    IApplicationDbContext db,
    EntitlementService entitlementService,
    IAuditService audit)
    : Endpoint<UpdateCoachDiscountRequest, CoachDiscountDto>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/payments/discounts/{DiscountId}");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Update a discount";
            s.Description = "Updates a discount and replaces the packages it is limited to.";
            s.Responses[StatusCodes.Status200OK] = "Updated discount";
            s.Responses[StatusCodes.Status400BadRequest] = "Invalid request body, a package is not available (DISCOUNT_PACKAGE_INVALID), or a fixed discount is limited to a package in another currency (CURRENCY_MISMATCH)";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or unreadable caller claim";
            s.Responses[StatusCodes.Status403Forbidden] = "The subscription does not include payments (PAYMENTS_NOT_ENTITLED), or the coach role was removed";
            s.Responses[StatusCodes.Status404NotFound] = "The discount does not exist or belongs to another coach, or the caller has no professional profile";
            s.Responses[StatusCodes.Status409Conflict] = "The discount is archived (DISCOUNT_ARCHIVED), or its currency changes while client terms use it (CURRENCY_LOCKED)";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(UpdateCoachDiscountRequest req, CancellationToken ct)
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

        var discount = await db.CoachDiscounts
            .Include(d => d.Packages).ThenInclude(l => l.CoachPackage)
            .FirstOrDefaultAsync(d => d.PublicId == req.DiscountId && d.ProfessionalProfileId == profileId, ct);

        if (discount is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (discount.ArchivedAt is not null)
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict, ErrorCodes.DiscountArchived, "The discount is archived.", ct);
            return;
        }

        var packages = await this.LoadActivePackagesOrRespondAsync(
            db, profileId.Value, req.Scope, req.PackageIds, ct);

        if (packages is null || await this.RespondIfCurrencyMismatchAsync(req.Type, req.Currency, packages, ct))
        {
            return;
        }

        var newCurrency = req.Type == DiscountType.FixedAmount ? req.Currency : null;

        var usage = await ClientPaymentSchedule.CountCurrentUsageAsync(db, profileId.Value, ct);

        if (newCurrency is not null && discount.Currency != newCurrency && usage.ByDiscount.ContainsKey(discount.Id))
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict,
                ErrorCodes.CurrencyLocked,
                "The currency cannot change while client terms use the discount.",
                ct);
            return;
        }

        var oldValues = JsonSerializer.Serialize(new { discount.Name, discount.Type, discount.Value });

        discount.Name = req.Name.Trim();
        discount.Type = req.Type;
        discount.Value = req.Value;
        discount.Currency = newCurrency;
        discount.Duration = req.Duration;
        discount.DurationPayments = req.DurationPayments;
        discount.ValidFrom = req.ValidFrom;
        discount.ValidTo = req.ValidTo;
        discount.Scope = req.Scope;

        ReplacePackageLinks(discount, packages);

        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            userId,
            "UpdateCoachDiscount",
            nameof(CoachDiscount),
            discount.PublicId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            oldValues: oldValues,
            newValues: JsonSerializer.Serialize(new { discount.Name, discount.Type, discount.Value }),
            ct: ct);

        await Send.OkAsync(CoachDiscountDto.FromEntity(discount, usage.ByDiscount.GetValueOrDefault(discount.Id)), ct);
    }

    private void ReplacePackageLinks(CoachDiscount discount, List<CoachPackage> packages)
    {
        var wantedIds = packages.Select(p => p.Id).ToHashSet();

        foreach (var link in discount.Packages.Where(l => !wantedIds.Contains(l.CoachPackageId)).ToList())
        {
            discount.Packages.Remove(link);
            db.CoachDiscountPackages.Remove(link);
        }

        var existingIds = discount.Packages.Select(l => l.CoachPackageId).ToHashSet();

        foreach (var package in packages.Where(p => !existingIds.Contains(p.Id)))
        {
            discount.Packages.Add(new CoachDiscountPackage { CoachPackage = package });
        }
    }
}
