using FormUp.Application.Domain.Extensions;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.ClientPhotos.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FormUp.Application.Features.ClientPhotos.GetTrainerClientPhotos;

/// <summary>
/// Returns a paginated, optionally month-grouped list of plan photos across ALL plans
/// for a specific client managed by the authenticated trainer or nutritionist.
/// </summary>
/// <remarks>
/// <para>
/// Authorization: the calling professional must have an active
/// <c>ClientProfessionalLink</c> to the client identified by <c>{ClientId}</c>.
/// A missing or inactive link results in a 404 (no information leakage about the
/// existence of the client record).
/// </para>
/// <para>
/// Pagination: controlled by <c>page</c> / <c>pageSize</c> query params.
/// The <c>X-Total-Count</c> header carries the total count of matching photos
/// (or month groups when <c>groupByMonth=true</c>).
/// </para>
/// </remarks>
/// <param name="db">Relational database context (PostgreSQL via EF Core).</param>
/// <param name="linkAuthorizationService">Resolves the caller's link capabilities to the
/// client identified by <c>{ClientId}</c>.</param>
/// <param name="blobStorage">Blob storage service — converts each stored BlobUrl into a
/// short-lived pre-signed read URL before the response leaves the process (F9).</param>
/// <param name="mongo">MongoDB context — resolves each meal photo's meal id and kind.</param>
public class GetTrainerClientPhotosEndpoint(
    IApplicationDbContext db,
    IClientLinkAuthorizationService linkAuthorizationService,
    IBlobStorageService blobStorage,
    IMongoContext mongo)
    : Endpoint<GetTrainerClientPhotosRequest, GetTrainerClientPhotosResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/trainer/clients/{ClientId}/photos");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "List a client's photos (trainer view)";
            s.Description = "Returns a paginated list of plan photos across all plans for a specific client. " +
                            "Requires an active trainer-client relationship. " +
                            "Supports optional plan filter (PlanId), category filter, date range filter, and optional month grouping. " +
                            "Total count (photos or groups) is in the X-Total-Count response header.";
            s.Responses[404] = "Client not found or no active relationship with the caller";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GetTrainerClientPhotosRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var trainerUserId = Guid.Parse(userId);

        if (await this.RespondIfCoachFeatureNotEntitledAsync(
                trainerUserId, entitlements => entitlements.CanUseProgressPhotos,
                "The current subscription does not include progress photos.", ct))
        {
            return;
        }

        // Verify active trainer-client link, and read its capability flags rather than only its
        // existence — the caller supplies the category filter, so an existence check alone let a
        // training-only professional select precisely the nutrition-domain rows their link denies.
        var capabilities = await linkAuthorizationService.GetCapabilitiesByClientPublicIdAsync(
            trainerUserId, req.ClientId, ct);

        if (capabilities is null || capabilities.Value.GrantsNothing)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Resolve the client profile — needed to scope the PlanPhotos query by ClientProfileId.
        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.PublicId == req.ClientId, ct);

        if (clientProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Build base query scoped to this client
        var query = db.PlanPhotos
            .AsNoTracking()
            .Where(p => p.ClientProfileId == clientProfile.Id
                && db.PlanPhotoViewers.Any(v => v.PlanPhotoId == p.Id && v.ProfessionalUserId == trainerUserId));

        // Domain scoping, keyed on Category alone — deliberately NOT on PlanType.
        //
        // Category is the authoritative signal for what a photo hangs off: Food is a meal-log
        // attachment in a nutrition plan, Training is a session attachment. Body and free-form
        // photos are standalone and stay dual-readable in both directions, matching how the
        // timeline endpoint already classifies body measurements.
        //
        // PlanType cannot be used for this. SaveDayPhotosEndpoint writes EVERY day photo — Body and
        // FreeForm included — with PlanType = Nutrition and the plan's id, because day photos are
        // uploaded through a nutrition-plan screen. Keying on PlanType therefore hid a client's
        // body-progress photos from a training-only coach: fail-closed, but a real loss of
        // dual-readable content rather than a leak being closed.
        //
        // Applied to the BASE query, before the caller's own category and plan filters, so no
        // request field can widen it.
        if (!capabilities.Value.CanViewNutritionPlans)
        {
            query = query.Where(p => p.Category != PlanPhotoCategory.Food);
        }

        if (!capabilities.Value.CanViewTrainingPlans)
        {
            query = query.Where(p => p.Category != PlanPhotoCategory.Training);
        }

        // Optional plan filter
        if (req.PlanId.HasValue)
        {
            query = query.Where(p => p.PlanId == req.PlanId.Value);
        }

        // Optional category filter
        if (req.Category.HasValue)
        {
            query = query.Where(p => p.Category == req.Category.Value);
        }

        // Optional date range filter on TakenAt (inclusive)
        // Query binding yields Local/Unspecified kinds; Npgsql only accepts UTC for timestamptz.
        if (req.From.HasValue)
        {
            var fromUtc = ToUtc(req.From.Value);
            query = query.Where(p => p.TakenAt >= fromUtc);
        }

        if (req.To.HasValue)
        {
            var toUtc = ToUtc(req.To.Value);
            query = query.Where(p => p.TakenAt <= toUtc);
        }

        if (req.GroupByMonth)
        {
            // Fetch all matching photos ordered by TakenAt DESC for grouping.
            // GroupBy + pagination cannot be efficiently translated to SQL for
            // this shape, so we load a minimal projection and group in .NET.
            var allPhotos = await query
                .OrderByDescending(p => p.TakenAt)
                .Select(p => new ClientPhotoResponse
                {
                    Id = p.PublicId,
                    BlobUrl = p.BlobUrl,
                    Description = p.Description,
                    Category = p.Category,
                    PlanId = p.PlanId,
                    PlanType = p.PlanType,
                    MealLogId = p.MealLogId,
                    TakenAt = p.TakenAt,
                    UploadedByUserId = p.UploadedByUserId,
                    UploadedAt = p.DateCreated,
                    DiaryRequestId = p.DiaryRequestId,
                })
                .ToListAsync(ct);

            // Group by YYYY-MM, preserving descending order within each group
            var groups = allPhotos
                .GroupBy(p => p.TakenAt.ToString("yyyy-MM"))
                .OrderByDescending(g => g.Key)
                .Select(g => new MonthGroupResponse
                {
                    YearMonth = g.Key,
                    Photos = g.ToList()
                })
                .ToList();

            var totalGroups = groups.Count;

            var pagedGroups = groups
                .Skip((req.Page - 1) * req.PageSize)
                .Take(req.PageSize)
                .ToList();

            // Sign only the photos actually being returned (post-pagination), not the full
            // in-memory grouping set — a stored BlobUrl is no longer publicly fetchable (F9).
            var pagedPhotos = pagedGroups.SelectMany(g => g.Photos).ToList();
            await SignPhotoUrlsAsync(pagedPhotos, ct);
            await ResolveMealsAsync(pagedPhotos, clientProfile.UserId, ct);
            await ResolvePlanNamesAsync(pagedPhotos, clientProfile.UserId, capabilities.Value, ct);

            HttpContext.Response.Headers["X-Total-Count"] = totalGroups.ToString();

            await Send.OkAsync(new GetTrainerClientPhotosResponse
            {
                Groups = pagedGroups
            }, ct);
        }
        else
        {
            var totalCount = await query.CountAsync(ct);

            var photos = await query
                .OrderByDescending(p => p.TakenAt)
                .Skip((req.Page - 1) * req.PageSize)
                .Take(req.PageSize)
                .Select(p => new ClientPhotoResponse
                {
                    Id = p.PublicId,
                    BlobUrl = p.BlobUrl,
                    Description = p.Description,
                    Category = p.Category,
                    PlanId = p.PlanId,
                    PlanType = p.PlanType,
                    MealLogId = p.MealLogId,
                    TakenAt = p.TakenAt,
                    UploadedByUserId = p.UploadedByUserId,
                    UploadedAt = p.DateCreated,
                    DiaryRequestId = p.DiaryRequestId,
                })
                .ToListAsync(ct);

            // A stored BlobUrl is no longer publicly fetchable — mint a short-lived read URL
            // for each photo before it leaves the process (F9).
            await SignPhotoUrlsAsync(photos, ct);
            await ResolveMealsAsync(photos, clientProfile.UserId, ct);
            await ResolvePlanNamesAsync(photos, clientProfile.UserId, capabilities.Value, ct);

            HttpContext.Response.Headers["X-Total-Count"] = totalCount.ToString();

            await Send.OkAsync(new GetTrainerClientPhotosResponse
            {
                Photos = photos
            }, ct);
        }
    }

    /// <summary>
    /// Populates each photo's <see cref="ClientPhotoResponse.DisplayUrl"/> with a short-lived
    /// pre-signed read URL. Must run on every response path before <c>Send.OkAsync</c> — the
    /// bucket no longer grants public read on the <c>plan-photos/</c> prefix these photos live
    /// under. <see cref="ClientPhotoResponse.BlobUrl"/> is left untouched — it stays the
    /// canonical, permanent identity value.
    /// </summary>
    private async Task SignPhotoUrlsAsync(IEnumerable<ClientPhotoResponse> photos, CancellationToken ct)
    {
        foreach (var photo in photos)
        {
            photo.DisplayUrl = await blobStorage.GenerateReadUrlAsync(photo.BlobUrl, ct) ?? string.Empty;
        }
    }

    /// <summary>
    /// Fills <see cref="ClientPhotoResponse.MealId"/> and <see cref="ClientPhotoResponse.MealKind"/>
    /// for the returned meal photos with at most two Mongo queries. Both lookups are scoped to the
    /// client, because a photo's MealLogId is client-supplied and must not resolve another
    /// client's data. Anything unresolvable is left null.
    /// </summary>
    private async Task ResolveMealsAsync(
        IReadOnlyList<ClientPhotoResponse> photos,
        Guid clientUserId,
        CancellationToken ct)
    {
        var logIdsByPhoto = new Dictionary<ClientPhotoResponse, ObjectId>();
        foreach (var photo in photos)
        {
            if (photo.Category == PlanPhotoCategory.Food
                && photo.MealLogId is not null
                && ObjectId.TryParse(photo.MealLogId, out var logId))
            {
                logIdsByPhoto[photo] = logId;
            }
        }

        if (logIdsByPhoto.Count == 0)
        {
            return;
        }

        var logFilter = Builders<MealLog>.Filter.And(
            Builders<MealLog>.Filter.In(log => log.Id, logIdsByPhoto.Values.Distinct()),
            Builders<MealLog>.Filter.Eq(log => log.ClientId, clientUserId));
        var logOptions = new FindOptions<MealLog, MealLogRef>
        {
            Projection = Builders<MealLog>.Projection.Expression(log => new MealLogRef(log.Id, log.PlanId, log.MealId))
        };

        using var logCursor = await mongo.MealLogs.FindAsync(logFilter, logOptions, ct);
        var logs = (await logCursor.ToListAsync(ct)).ToDictionary(log => log.Id);

        if (logs.Count == 0)
        {
            return;
        }

        var planFilter = Builders<NutritionPlan>.Filter.And(
            Builders<NutritionPlan>.Filter.In(plan => plan.ExternalId, logs.Values.Select(log => log.PlanId).Distinct()),
            Builders<NutritionPlan>.Filter.Eq(plan => plan.ClientId, clientUserId));
        var planOptions = new FindOptions<NutritionPlan, PlanMealsRef>
        {
            Projection = Builders<NutritionPlan>.Projection.Expression(plan => new PlanMealsRef(
                plan.ExternalId,
                plan.Weeks.SelectMany(week => week.Days).SelectMany(day => day.Meals)
                    .Select(meal => new PlanMealRef(meal.MealId, meal.Kind)).ToList()))
        };

        using var planCursor = await mongo.NutritionPlans.FindAsync(planFilter, planOptions, ct);
        var plans = await planCursor.ToListAsync(ct);

        var kindByMeal = new Dictionary<(Guid PlanId, Guid MealId), MealKind>();
        foreach (var plan in plans)
        {
            foreach (var meal in plan.Meals)
            {
                kindByMeal[(plan.PlanId, meal.MealId)] = meal.Kind;
            }
        }

        foreach (var (photo, logId) in logIdsByPhoto)
        {
            if (!logs.TryGetValue(logId, out var log))
            {
                continue;
            }

            photo.MealId = log.MealId;
            photo.MealKind = kindByMeal.TryGetValue((log.PlanId, log.MealId), out var kind) ? kind : null;
        }
    }

    /// <summary>
    /// Fills <see cref="ClientPhotoResponse.PlanName"/> with at most one Mongo query per plan type.
    /// A plan type is only looked up when the caller may view it, because Body and free-form photos
    /// stay visible without the matching capability. Lookups are scoped to the client, so a
    /// client-supplied plan id cannot resolve another client's plan. Unresolvable ids stay null.
    /// </summary>
    private async Task ResolvePlanNamesAsync(
        IReadOnlyList<ClientPhotoResponse> photos,
        Guid clientUserId,
        LinkCapabilities capabilities,
        CancellationToken ct)
    {
        var nutritionPlanIds = capabilities.CanViewNutritionPlans
            ? PlanIdsOfType(photos, PlanPhotoType.Nutrition)
            : [];
        var trainingPlanIds = capabilities.CanViewTrainingPlans
            ? PlanIdsOfType(photos, PlanPhotoType.Training)
            : [];

        var nutritionNames = new Dictionary<Guid, string>();
        if (nutritionPlanIds.Count > 0)
        {
            var filter = Builders<NutritionPlan>.Filter.And(
                Builders<NutritionPlan>.Filter.In(plan => plan.ExternalId, nutritionPlanIds),
                Builders<NutritionPlan>.Filter.Eq(plan => plan.ClientId, clientUserId));
            var options = new FindOptions<NutritionPlan, PlanNameRef>
            {
                Projection = Builders<NutritionPlan>.Projection.Expression(
                    plan => new PlanNameRef(plan.ExternalId, plan.Name))
            };

            using var cursor = await mongo.NutritionPlans.FindAsync(filter, options, ct);
            nutritionNames = (await cursor.ToListAsync(ct)).ToDictionary(plan => plan.PlanId, plan => plan.Name);
        }

        var trainingNames = new Dictionary<Guid, string>();
        if (trainingPlanIds.Count > 0)
        {
            var filter = Builders<TrainingPlan>.Filter.And(
                Builders<TrainingPlan>.Filter.In(plan => plan.ExternalId, trainingPlanIds),
                Builders<TrainingPlan>.Filter.Eq(plan => plan.ClientId, clientUserId));
            var options = new FindOptions<TrainingPlan, PlanNameRef>
            {
                Projection = Builders<TrainingPlan>.Projection.Expression(
                    plan => new PlanNameRef(plan.ExternalId, plan.Name))
            };

            using var cursor = await mongo.TrainingPlans.FindAsync(filter, options, ct);
            trainingNames = (await cursor.ToListAsync(ct)).ToDictionary(plan => plan.PlanId, plan => plan.Name);
        }

        foreach (var photo in photos)
        {
            if (photo.PlanId is not { } planId)
            {
                continue;
            }

            var names = photo.PlanType == PlanPhotoType.Training ? trainingNames : nutritionNames;
            photo.PlanName = names.TryGetValue(planId, out var name) ? name : null;
        }
    }

    private static List<Guid> PlanIdsOfType(IEnumerable<ClientPhotoResponse> photos, PlanPhotoType planType) =>
        photos
            .Where(photo => photo.PlanType == planType && photo.PlanId.HasValue)
            .Select(photo => photo.PlanId!.Value)
            .Distinct()
            .ToList();

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    private sealed record MealLogRef(ObjectId Id, Guid PlanId, Guid MealId);

    private sealed record PlanNameRef(Guid PlanId, string Name);

    private sealed record PlanMealRef(Guid MealId, MealKind Kind);

    private sealed record PlanMealsRef(Guid PlanId, List<PlanMealRef> Meals);
}
