using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Decides at upload time which coaches may see a newly inserted <see cref="PlanPhoto"/> and adds
/// the matching <see cref="PlanPhotoViewer"/> rows to the context. The caller saves them in the
/// same <c>SaveChangesAsync</c> as the photo insert.
/// </summary>
/// <remarks>
/// Food and Training photos go to the plan's creator only, even when that coach's link is dead.
/// Body and FreeForm photos go to every coach whose link is live at the moment of the call (the
/// same rule as <see cref="ClientLinkAuthorizationService"/>: active link granting at least one
/// domain), whether or not they carry a plan id.
/// </remarks>
public static class PlanPhotoVisibilityWriter
{
    /// <summary>
    /// Adds viewer rows for photos that are about to be inserted.
    /// </summary>
    /// <param name="db">Relational database context.</param>
    /// <param name="logger">Logger for the skipped-creator case.</param>
    /// <param name="photos">Newly inserted, not yet saved photos of one client.</param>
    /// <param name="planCreatorUserId">The plan's creator (<c>ApplicationUser.Id</c>), or null or
    /// <see cref="Guid.Empty"/> when unknown.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task AddViewersAsync(
        IApplicationDbContext db,
        ILogger logger,
        IReadOnlyCollection<PlanPhoto> photos,
        Guid? planCreatorUserId,
        CancellationToken ct)
    {
        if (photos.Count == 0)
        {
            return;
        }

        IReadOnlyList<Guid>? linkedCoaches = null;

        foreach (var photo in photos)
        {
            if (photo.Category is PlanPhotoCategory.Food or PlanPhotoCategory.Training)
            {
                if (planCreatorUserId is null || planCreatorUserId == Guid.Empty)
                {
                    logger.LogWarning(
                        "Plan photo {PhotoId} has no resolvable plan creator; no viewer row written",
                        photo.PublicId);
                    continue;
                }

                AddViewer(db, photo, planCreatorUserId.Value);
                continue;
            }

            linkedCoaches ??= await LoadLiveCoachesAsync(db, photo.ClientProfileId, ct);

            foreach (var coachUserId in linkedCoaches)
            {
                AddViewer(db, photo, coachUserId);
            }
        }
    }

    private static void AddViewer(IApplicationDbContext db, PlanPhoto photo, Guid coachUserId) =>
        db.PlanPhotoViewers.Add(new PlanPhotoViewer
        {
            PlanPhoto = photo,
            ProfessionalUserId = coachUserId,
        });

    private static async Task<IReadOnlyList<Guid>> LoadLiveCoachesAsync(
        IApplicationDbContext db, long clientProfileId, CancellationToken ct) =>
        await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(link => link.ClientProfileId == clientProfileId
                && link.ConnectionState != ConnectionState.Ended
                && (link.CanViewNutritionPlans || link.CanViewTrainingPlans))
            .Join(
                db.ProfessionalProfiles,
                link => link.ProfessionalProfileId,
                profile => profile.Id,
                (_, profile) => profile.UserId)
            .Distinct()
            .ToListAsync(ct);
}
