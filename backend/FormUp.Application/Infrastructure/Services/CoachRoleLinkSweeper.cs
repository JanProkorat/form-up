using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace FormUp.Application.Infrastructure.Services;

/// <summary>
/// Hourly job that ends client links covered only by a removed coach role once the client has no
/// in-progress plan of that discipline with the coach. Stateless and idempotent: each tick re-derives
/// everything, and a link is flipped inactive by a conditional update so a concurrent end wins once.
/// </summary>
/// <param name="scopeFactory">Creates one scope per profile and per link.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="logger">Logger.</param>
public class CoachRoleLinkSweeper(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<CoachRoleLinkSweeper> logger) : BackgroundService
{
    private sealed record LinkCandidate(
        long LinkId,
        Guid LinkPublicId,
        Guid ClientUserId,
        bool GrantsTraining,
        bool GrantsNutrition);

    private sealed record PlanWindow(Guid ClientId, DateTime? StartDate, int WeekCount);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));

        do
        {
            try
            {
                await TickAsync(timeProvider.GetUtcNow().UtcDateTime, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "CoachRoleLinkSweeper: unhandled error during tick.");
            }
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    /// <summary>Runs one sweep treating <paramref name="now"/> as the current UTC time.</summary>
    /// <param name="now">Current UTC time.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task TickAsync(DateTime now, CancellationToken ct)
    {
        await RemoveRolesOfEndedAccountsAsync(now, ct);

        List<long> profileIds;

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            profileIds = await db.ProfessionalProfiles
                .AsNoTracking()
                .Where(p => p.TrainerRoleRemovedAt != null || p.NutritionistRoleRemovedAt != null)
                .Select(p => p.Id)
                .ToListAsync(ct);
        }

        foreach (var profileId in profileIds)
        {
            try
            {
                await SweepProfileAsync(profileId, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "CoachRoleLinkSweeper: failed to sweep professional profile {ProfileId}.", profileId);
            }
        }
    }

    /// <summary>
    /// Ids of profiles whose coach account has ended and whose user still holds a coach role that is
    /// not yet marked removed.
    /// </summary>
    /// <param name="now">Current UTC time.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task<List<long>> SelectEndedAccountProfileIdsAsync(DateTime now, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

        var heldCoachRoles = db.UserRoles
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, RoleName = r.Name });

        return await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(p => p.CoachAccountActiveUntil != null && p.CoachAccountActiveUntil <= now)
            .Where(p =>
                (p.TrainerRoleRemovedAt == null
                 && heldCoachRoles.Any(h => h.UserId == p.UserId && h.RoleName == AppRoles.Trainer))
                || (p.NutritionistRoleRemovedAt == null
                    && heldCoachRoles.Any(h => h.UserId == p.UserId && h.RoleName == AppRoles.Nutritionist)))
            .Select(p => p.Id)
            .ToListAsync(ct);
    }

    private async Task RemoveRolesOfEndedAccountsAsync(DateTime now, CancellationToken ct)
    {
        var profileIds = await SelectEndedAccountProfileIdsAsync(now, ct);

        foreach (var profileId in profileIds)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var remover = scope.ServiceProvider.GetRequiredService<ICoachRoleRemover>();

                var profile = await db.ProfessionalProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId, ct);
                var user = profile is null ? null : await userManager.FindByIdAsync(profile.UserId.ToString());

                if (profile is null || user is null)
                {
                    continue;
                }

                var activeRoles = CoachRoleStatus.ActiveRoles(await userManager.GetRolesAsync(user), profile);

                if (activeRoles.Count > 0)
                {
                    await remover.RemoveAsync(user, profileId, activeRoles, now, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "CoachRoleLinkSweeper: failed to end coach account of profile {ProfileId}.", profileId);
            }
        }
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task SweepProfileAsync(long profileId, DateTime now, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var profile = await db.ProfessionalProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId, ct);
        var user = profile is null ? null : await userManager.FindByIdAsync(profile.UserId.ToString());

        if (profile is null || user is null)
        {
            return;
        }

        var trainingRemoved = CoachRoleStatus.IsRemoved(profile, AppRoles.Trainer);
        var nutritionRemoved = CoachRoleStatus.IsRemoved(profile, AppRoles.Nutritionist);

        if (!trainingRemoved && !nutritionRemoved)
        {
            return;
        }

        var activeRoles = CoachRoleStatus.ActiveRoles(await userManager.GetRolesAsync(user), profile);
        var trainingActive = activeRoles.Contains(AppRoles.Trainer);
        var nutritionActive = activeRoles.Contains(AppRoles.Nutritionist);

        var links = await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(l => l.ProfessionalProfileId == profileId && l.ConnectionState != ConnectionState.Ended)
            .Select(l => new
            {
                l.Id,
                l.PublicId,
                l.CanViewTrainingPlans,
                l.CanViewNutritionPlans,
                ClientUserId = l.ClientProfile.UserId,
            })
            .ToListAsync(ct);

        var candidates = links
            .Where(l => (trainingRemoved && l.CanViewTrainingPlans) || (nutritionRemoved && l.CanViewNutritionPlans))
            .Where(l => !((l.CanViewTrainingPlans && trainingActive) || (l.CanViewNutritionPlans && nutritionActive)))
            .Select(l => new LinkCandidate(
                l.Id,
                l.PublicId,
                l.ClientUserId,
                trainingRemoved && l.CanViewTrainingPlans,
                nutritionRemoved && l.CanViewNutritionPlans))
            .ToList();

        if (candidates.Count == 0)
        {
            return;
        }

        var today = DateOnly.FromDateTime(now);
        var trainingInProgress = candidates.Any(c => c.GrantsTraining)
            ? await LoadTrainingInProgressClientsAsync(mongo, profile.UserId, today, ct)
            : [];
        var nutritionInProgress = candidates.Any(c => c.GrantsNutrition)
            ? await LoadNutritionInProgressClientsAsync(mongo, profile.UserId, today, ct)
            : [];

        foreach (var candidate in candidates)
        {
            var inProgress = (candidate.GrantsTraining && trainingInProgress.Contains(candidate.ClientUserId))
                             || (candidate.GrantsNutrition && nutritionInProgress.Contains(candidate.ClientUserId));

            if (inProgress)
            {
                continue;
            }

            try
            {
                await EndLinkAsync(profileId, profile.UserId, candidate, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "CoachRoleLinkSweeper: failed to end link {LinkId}.", candidate.LinkId);
            }
        }
    }

    private static async Task<HashSet<Guid>> LoadTrainingInProgressClientsAsync(
        IMongoContext mongo, Guid trainerUserId, DateOnly today, CancellationToken ct)
    {
        var plans = await mongo.TrainingPlans
            .Find(Builders<TrainingPlan>.Filter.Eq(p => p.TrainerId, trainerUserId)
                  & Builders<TrainingPlan>.Filter.Eq(p => p.Status, TrainingPlanStatus.Active))
            .Project(p => new PlanWindow(p.ClientId, p.StartDate, p.Weeks.Count))
            .ToListAsync(ct);

        return InProgressClients(plans, today);
    }

    private static async Task<HashSet<Guid>> LoadNutritionInProgressClientsAsync(
        IMongoContext mongo, Guid nutritionistUserId, DateOnly today, CancellationToken ct)
    {
        var plans = await mongo.NutritionPlans
            .Find(Builders<NutritionPlan>.Filter.Eq(p => p.NutritionistId, nutritionistUserId)
                  & Builders<NutritionPlan>.Filter.Eq(p => p.Status, NutritionPlanStatus.Active))
            .Project(p => new PlanWindow(p.ClientId, p.StartDate, p.Weeks.Count))
            .ToListAsync(ct);

        return InProgressClients(plans, today);
    }

    private static HashSet<Guid> InProgressClients(List<PlanWindow> plans, DateOnly today) =>
        plans
            .Where(plan => PlanWindowResolver.HasNotEnded(plan.StartDate, plan.WeekCount, today))
            .Select(plan => plan.ClientId)
            .ToHashSet();

    private async Task EndLinkAsync(
        long profileId, Guid professionalUserId, LinkCandidate candidate, DateTime now, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        // Re-check inside the link's own scope: adding the role back between the sweep's read and
        // now must keep the link.
        var markers = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(p => p.Id == profileId)
            .Select(p => new { p.TrainerRoleRemovedAt, p.NutritionistRoleRemovedAt })
            .FirstOrDefaultAsync(ct);

        if (markers is null
            || (candidate.GrantsTraining && markers.TrainerRoleRemovedAt is null)
            || (candidate.GrantsNutrition && markers.NutritionistRoleRemovedAt is null))
        {
            return;
        }

        // Archive before flipping, like EndCollaboration: a failed Mongo write leaves the link
        // active so the next tick retries.
        await CollaborationPlanArchiver.ArchiveAsync(
            mongo,
            professionalUserId,
            candidate.ClientUserId,
            archiveNutrition: candidate.GrantsNutrition,
            archiveTraining: candidate.GrantsTraining,
            now,
            ct);

        var clientProfileId = await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(l => l.Id == candidate.LinkId)
            .Select(l => l.ClientProfileId)
            .FirstAsync(ct);

        await ClientPaymentTermsCloser.CloseAsync(
            db,
            scope.ServiceProvider.GetRequiredService<IAuditService>(),
            profileId,
            clientProfileId,
            professionalUserId,
            now,
            ct);

        var changed = await db.ClientProfessionalLinks
            .Where(l => l.Id == candidate.LinkId && l.ConnectionState != ConnectionState.Ended)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(l => l.ConnectionState, ConnectionState.Ended)
                    .SetProperty(l => l.DateUpdated, (DateTime?)now),
                ct);

        if (changed != 1)
        {
            return;
        }

        var names = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == candidate.ClientUserId || u.Id == professionalUserId)
            .Select(u => new { u.Id, u.FirstName, u.LastName })
            .ToListAsync(ct);

        var clientName = names.Where(u => u.Id == candidate.ClientUserId)
            .Select(u => $"{u.FirstName} {u.LastName}".Trim()).FirstOrDefault() ?? string.Empty;
        var coachName = names.Where(u => u.Id == professionalUserId)
            .Select(u => $"{u.FirstName} {u.LastName}".Trim()).FirstOrDefault() ?? string.Empty;

        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var notifier = scope.ServiceProvider.GetRequiredService<IRealtimeNotifier>();

        await NotifyAsync(
            notifications, notifier, candidate.ClientUserId,
            NotificationType.CollaborationEndedByRoleRemoval,
            new Dictionary<string, string> { ["coachName"] = coachName }, ct);

        await NotifyAsync(
            notifications, notifier, professionalUserId,
            NotificationType.CollaborationEndedByRoleRemovalCoach,
            new Dictionary<string, string> { ["clientName"] = clientName }, ct);

        try
        {
            await notifier.NotifyAsync(
                professionalUserId,
                "collaborationended",
                new { LinkPublicId = candidate.LinkPublicId, ClientName = clientName },
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "CoachRoleLinkSweeper: failed to emit collaborationended for link {LinkId}.", candidate.LinkId);
        }

        logger.LogInformation("CoachRoleLinkSweeper: ended link {LinkId} after coach role removal.", candidate.LinkId);
    }

    private async Task NotifyAsync(
        INotificationService notifications,
        IRealtimeNotifier notifier,
        Guid recipientUserId,
        NotificationType type,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct)
    {
        try
        {
            var notification = await notifications.CreateAsync(recipientUserId, type, parameters, ct: ct);

            await notifier.NotifyAsync(
                recipientUserId,
                "newnotification",
                new { id = notification.Id, type = type.ToString(), data = notification.Data },
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "CoachRoleLinkSweeper: failed to notify user {UserId} ({Type}).", recipientUserId, type);
        }
    }
}
