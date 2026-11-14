using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.ClientMeasurements.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.ClientMeasurements.AddClientMeasurement;

/// <summary>
/// Allows a trainer or nutritionist to record a body measurement on behalf of
/// a linked client (e.g. values measured in person during a session).
/// Verifies an active professional-client link exists before granting access.
/// </summary>
/// <param name="db">Database context.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="linkAuthorizationService">Link capability service — measurements are not
/// domain-specific, so any active link (regardless of which domain(s) it grants) is sufficient.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
public class AddClientMeasurementEndpoint(
    IApplicationDbContext db, IAuditService audit, IClientLinkAuthorizationService linkAuthorizationService)
    : Endpoint<AddClientMeasurementRequest, MeasurementDto>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/trainer/clients/{ClientId}/measurements");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Record a client's body measurement";
            s.Description = "Creates a new body measurement for the specified client on behalf of the trainer/nutritionist. Requires an active trainer-client relationship.";
            s.Response<MeasurementDto>(201, "Measurement created");
            s.Responses[401] = "Unauthorized";
            s.Responses[404] = "Client not found or no active relationship";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(AddClientMeasurementRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var trainerId = Guid.Parse(userId);

        if (await this.RespondIfCoachFeatureNotEntitledAsync(
                trainerId, entitlements => entitlements.CanUseWeightAndMeasurements,
                "The current subscription does not include weight and measurements.", ct))
        {
            return;
        }

        var clientProfile = await db.ClientProfiles
            .FirstOrDefaultAsync(cp => cp.PublicId == req.ClientId, ct);

        if (clientProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Verify an active professional-client link exists. Measurements are not domain-specific
        // (neither training- nor nutrition-only) — the pre-migration check gated on IsActive
        // alone, with no capability-flag requirement, so any active link (regardless of which
        // domain(s) it grants) remains sufficient here.
        var capabilities = await linkAuthorizationService.GetCapabilitiesByClientPublicIdAsync(
            trainerId, req.ClientId, ct);

        if (capabilities is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var entity = new BodyMeasurement
        {
            ClientProfileId = clientProfile.Id,
            MeasuredAt = req.MeasuredAt,
            WeightKg = req.WeightKg,
            BodyFatPercentage = req.BodyFatPercentage,
            ChestCm = req.ChestCm,
            WaistCm = req.WaistCm,
            HipsCm = req.HipsCm,
            BicepsCm = req.BicepsCm,
            ThighsCm = req.ThighsCm,
            Notes = req.Notes
        };

        db.BodyMeasurements.Add(entity);
        await db.SaveChangesAsync(ct);

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        await audit.LogAsync(
            trainerId,
            "Create",
            nameof(BodyMeasurement),
            clientProfile.PublicId,
            ipAddress,
            ct: ct);

        await Send.ResponseAsync(MeasurementDto.FromEntity(entity), StatusCodes.Status201Created, ct);
    }
}
