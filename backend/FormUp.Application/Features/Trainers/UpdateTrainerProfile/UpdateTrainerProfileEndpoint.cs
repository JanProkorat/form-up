using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Trainers.UpdateTrainerProfile;

/// <summary>
/// Updates the professional profile for the currently authenticated trainer or nutritionist.
/// </summary>
/// <param name="db">Database context.</param>
/// <param name="audit">Audit logging service.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Exempt, Reason = "The coach's own profile stays editable while no coach role is active.")]
public class UpdateProfessionalProfileEndpoint(IApplicationDbContext db, IAuditService audit)
    : Endpoint<UpdateProfessionalProfileRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/trainer/profile");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Update professional profile";
            s.Description = "Updates the professional's profile (bio, specialization).";
            s.Response(StatusCodes.Status200OK, "Profile updated");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,"Invalid request");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The caller is not a trainer or nutritionist");
            s.Response(StatusCodes.Status404NotFound, "Professional profile not found");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(UpdateProfessionalProfileRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var userGuid = Guid.Parse(userId);

        var profile = await db.ProfessionalProfiles
            .FirstOrDefaultAsync(pp => pp.UserId == userGuid, ct);

        if (profile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        profile.Bio = req.Bio;
        profile.Specialization = req.Specialization;
        profile.City = req.City;
        profile.Specializations = req.Specializations;
        profile.Certificates = req.Certificates;
        profile.Languages = req.Languages;
        profile.CollaborationType = req.CollaborationType;
        profile.LinkedIn = req.LinkedIn;
        profile.Instagram = req.Instagram;
        profile.Website = req.Website;
        profile.ShowInSearch = req.ShowInSearch;
        profile.AcceptNewClients = req.AcceptNewClients;

        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            userGuid,
            "UpdateProfessionalProfile",
            nameof(ProfessionalProfile),
            profile.PublicId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        await Send.OkAsync(new { Message = "Professional profile updated successfully." }, ct);
    }
}
