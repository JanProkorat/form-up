using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.ClientPhotos.Shared;
using FormUp.Application.Features.PhotoDiaryRequests;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FormUp.Application.Features.ClientPhotos.FinalizeUnlinkedPhoto;

/// <summary>
/// Finalizes a progress photo upload that belongs to no plan by inserting a <see cref="PlanPhoto"/>
/// row with no plan, type or link. The blob must sit under
/// <c>plan-photos/unlinked/{callerUserId}/</c>; any other prefix is rejected so a client cannot
/// register another user's blob. Coaches with a live link at upload time can see the photo through
/// the viewer rows written here. When <see cref="FinalizeUnlinkedPhotoRequest.DiaryRequestId"/> is set
/// the photo answers that photo diary request, under the same rules as a plan photo.
/// </summary>
/// <param name="db">Relational database context for profile lookup and photo insert.</param>
/// <param name="notifier">Realtime notifier for the photoDiaryPhotoUploaded event.</param>
/// <param name="linkAuthorizationService">Link capability service — gates the coach-addressed event on a live link.</param>
/// <param name="logger">Logger.</param>
/// <param name="blobStorage">Blob storage service — canonicalizes the submitted URL and signs the response URL.</param>
public class FinalizeUnlinkedPhotoEndpoint(
    IApplicationDbContext db,
    IRealtimeNotifier notifier,
    IClientLinkAuthorizationService linkAuthorizationService,
    ILogger<FinalizeUnlinkedPhotoEndpoint> logger,
    IBlobStorageService blobStorage)
    : Endpoint<FinalizeUnlinkedPhotoRequest, PlanPhotoResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/client/photos");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Finalize progress photo upload (no plan)";
            s.Description =
                "Inserts a PlanPhoto row with no plan after the client has PUT the blob to the pre-signed URL "
                + "from POST /client/photos/upload-url. Category must be Body or FreeForm. The blob must be "
                + "under plan-photos/unlinked/{userId}/ for the calling client. When DiaryRequestId is provided "
                + "the photo answers that photo diary request: it must belong to the caller and be Accepted or "
                + "InProgress, and the first upload moves an Accepted request to InProgress.";
            s.Response<PlanPhotoResponse>(StatusCodes.Status201Created, "Progress photo finalized");
            s.Response(StatusCodes.Status400BadRequest, "Invalid request, or blob URL not under the caller's unlinked prefix");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status404NotFound,
                "Caller has no client profile, or the diary request is unknown or not the caller's");
            s.Response(StatusCodes.Status409Conflict,
                "The diary request is not Accepted/InProgress (PHOTO_DIARY_REQUEST_INVALID_STATUS), or its window has expired (PHOTO_DIARY_REQUEST_EXPIRED)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(FinalizeUnlinkedPhotoRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        var emailClaim = User.FindFirstValue(AppClaims.Email);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var callerUserId = Guid.Parse(userId);

        // Normalise to the canonical stored form, then bind the blob to the caller: the canonical URL
        // must start with the caller's own unlinked prefix and the remainder must be a bare file name.
        var canonicalBlobUrl = blobStorage.NormalizeToCanonicalUrl(req.BlobUrl);
        var callerPrefix = blobStorage.BuildPublicUrl($"plan-photos/unlinked/{callerUserId}/");

        if (canonicalBlobUrl is null
            || !canonicalBlobUrl.StartsWith(callerPrefix, StringComparison.Ordinal)
            || canonicalBlobUrl[callerPrefix.Length..].Contains('/', StringComparison.Ordinal))
        {
            await this.SendProblemAsync(400, ErrorCodes.InvalidBlobUrl,
                "Photo URL must be under the caller's unlinked photo storage prefix.", ct);
            return;
        }

        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.UserId == callerUserId, ct);

        if (clientProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        PhotoDiaryRequest? diaryRequest = null;

        if (req.DiaryRequestId.HasValue)
        {
            diaryRequest = await this.LoadForUploadOrRespondAsync(
                db, req.DiaryRequestId.Value, callerUserId, emailClaim, DateTimeOffset.UtcNow, ct);

            if (diaryRequest is null)
            {
                return;
            }
        }

        var now = DateTime.UtcNow;

        var photo = new PlanPhoto
        {
            PublicId = Guid.NewGuid(),
            ClientProfileId = clientProfile.Id,
            Category = req.Category,
            BlobUrl = canonicalBlobUrl,
            Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
            TakenAt = req.TakenAt ?? now,
            UploadedByUserId = callerUserId,
            DiaryRequestId = req.DiaryRequestId,
            DateCreated = now,
            DateUpdated = now
        };

        db.PlanPhotos.Add(photo);
        await PlanPhotoVisibilityWriter.AddViewersAsync(db, logger, [photo], null, ct);

        // Transition Accepted → InProgress on first photo upload
        if (diaryRequest is { Status: PhotoDiaryStatus.Accepted })
        {
            diaryRequest.Status = PhotoDiaryStatus.InProgress;
            diaryRequest.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        // A photo that answers a request is skipped by the burst; a free-form one joins the grouped email burst.
        await ProgressPhotoBurst.QueueAsync(db, logger, photo, clientProfile.PublicId, ct);

        if (diaryRequest is not null)
        {
            await NotifyRequestingCoachAsync(diaryRequest, photo, callerUserId, emailClaim, ct);
        }

        await Send.ResponseAsync(new PlanPhotoResponse
        {
            Id = photo.PublicId,
            BlobUrl = photo.BlobUrl,
            DisplayUrl = await blobStorage.GenerateReadUrlAsync(photo.BlobUrl, ct) ?? string.Empty,
            Category = photo.Category,
            Description = photo.Description,
            TakenAt = photo.TakenAt,
            DateCreated = photo.DateCreated,
            UploadedByUserId = photo.UploadedByUserId,
            DiaryRequestId = photo.DiaryRequestId
        }, StatusCodes.Status201Created, ct);
    }

    /// <summary>
    /// Tells the requesting coach about the new photo while their link to the client is live.
    /// Best-effort: a failure is logged and never fails the already-committed upload.
    /// </summary>
    private async Task NotifyRequestingCoachAsync(
        PhotoDiaryRequest diaryRequest,
        PlanPhoto photo,
        Guid callerUserId,
        string? emailClaim,
        CancellationToken ct)
    {
        try
        {
            var capabilities = await linkAuthorizationService.GetCapabilitiesByClientUserIdAsync(
                diaryRequest.ProfessionalId, callerUserId, ct);

            if (capabilities is null)
            {
                logger.LogWarning(
                    "Professional {ProfessionalId} has no live link to client {ClientId}; photoDiaryPhotoUploaded event skipped",
                    diaryRequest.ProfessionalId, callerUserId);
                return;
            }

            var dayIndex = diaryRequest.AcceptedAt.HasValue
                ? Math.Max(1, (DateTimeOffset.UtcNow - diaryRequest.AcceptedAt.Value).Days + 1)
                : 1;

            await notifier.NotifyAsync(
                diaryRequest.ProfessionalId,
                "photodiaryphotouploaded",
                new PhotoDiaryPhotoUploadedEvent
                {
                    RequestId = diaryRequest.Id,
                    PhotoId = photo.PublicId,
                    ClientName = PhotoDiaryRequestUploadGuard.ResolveClientName(diaryRequest, emailClaim),
                    DayIndex = dayIndex,
                    Caption = photo.Description,
                    UploadedAt = DateTimeOffset.UtcNow,
                },
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to emit photoDiaryPhotoUploaded for request {RequestId} to professional {ProfessionalId}",
                diaryRequest.Id, diaryRequest.ProfessionalId);
        }
    }
}
