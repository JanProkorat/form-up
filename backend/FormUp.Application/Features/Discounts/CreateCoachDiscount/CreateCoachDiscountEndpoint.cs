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

namespace FormUp.Application.Features.Discounts.CreateCoachDiscount;

/// <summary>
/// Creates a discount owned by the calling coach.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="entitlementService">Resolves subscription entitlements.</param>
/// <param name="audit">Audit logging service.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
internal sealed class CreateCoachDiscountEndpoint(
    IApplicationDbContext db,
    EntitlementService entitlementService,
    IAuditService audit)
    : Endpoint<CreateCoachDiscountRequest, CoachDiscountDto>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/payments/discounts");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Create a discount";
            s.Description = "Creates a percentage or fixed-amount discount for all or some of the coach's packages.";
            s.Response<CoachDiscountDto>(StatusCodes.Status201Created, "Created discount");
            s.Responses[StatusCodes.Status400BadRequest] = "Invalid request body, a package is not available (DISCOUNT_PACKAGE_INVALID), or a fixed discount is limited to a package in another currency (CURRENCY_MISMATCH)";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or unreadable caller claim";
            s.Responses[StatusCodes.Status403Forbidden] = "The subscription does not include payments (PAYMENTS_NOT_ENTITLED), or the coach role was removed";
            s.Responses[StatusCodes.Status404NotFound] = "The caller has no professional profile";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CreateCoachDiscountRequest req, CancellationToken ct)
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

        var packages = await this.LoadActivePackagesOrRespondAsync(
            db, profileId.Value, req.Scope, req.PackageIds, ct);

        if (packages is null || await this.RespondIfCurrencyMismatchAsync(req.Type, req.Currency, packages, ct))
        {
            return;
        }

        var discount = new CoachDiscount
        {
            PublicId = Guid.NewGuid(),
            ProfessionalProfileId = profileId.Value,
            Name = req.Name.Trim(),
            Type = req.Type,
            Value = req.Value,
            Currency = req.Type == DiscountType.FixedAmount ? req.Currency : null,
            Duration = req.Duration,
            DurationPayments = req.DurationPayments,
            ValidFrom = req.ValidFrom,
            ValidTo = req.ValidTo,
            Scope = req.Scope
        };

        foreach (var package in packages)
        {
            discount.Packages.Add(new CoachDiscountPackage { CoachPackage = package });
        }

        db.CoachDiscounts.Add(discount);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            userId,
            "CreateCoachDiscount",
            nameof(CoachDiscount),
            discount.PublicId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            newValues: JsonSerializer.Serialize(new { discount.Name, discount.Type, discount.Value }),
            ct: ct);

        await Send.ResponseAsync(CoachDiscountDto.FromEntity(discount), StatusCodes.Status201Created, ct);
    }
}
