using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Default <see cref="ICoachRoleRemover"/>, shared by the remove-role endpoint, the disable-account
/// endpoint and the period-end sweeper.
/// </summary>
/// <param name="db">Database context.</param>
/// <param name="notificationService">Persisted-notification service.</param>
/// <param name="notifier">Realtime notifier.</param>
/// <param name="logger">Logger.</param>
public class CoachRoleRemover(
    IApplicationDbContext db,
    INotificationService notificationService,
    IRealtimeNotifier notifier,
    ILogger<CoachRoleRemover> logger) : ICoachRoleRemover
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> RemoveAsync(
        ApplicationUser coach,
        long professionalProfileId,
        IReadOnlyCollection<string> roles,
        DateTime now,
        CancellationToken ct)
    {
        var removed = new List<string>();

        foreach (var role in roles.Distinct())
        {
            var query = db.ProfessionalProfiles.Where(p => p.Id == professionalProfileId);
            var changed = role == AppRoles.Trainer
                ? await query.Where(p => p.TrainerRoleRemovedAt == null).ExecuteUpdateAsync(
                    setters => setters.SetProperty(p => p.TrainerRoleRemovedAt, (DateTime?)now), ct)
                : await query.Where(p => p.NutritionistRoleRemovedAt == null).ExecuteUpdateAsync(
                    setters => setters.SetProperty(p => p.NutritionistRoleRemovedAt, (DateTime?)now), ct);

            if (changed == 1)
            {
                removed.Add(role);
            }
        }

        if (removed.Count > 0)
        {
            await NotifyClientsAsync(coach, professionalProfileId, removed, ct);
        }

        return removed;
    }

    /// <inheritdoc />
    public async Task NotifyClientsAsync(
        ApplicationUser coach,
        long professionalProfileId,
        IReadOnlyCollection<string> roles,
        CancellationToken ct)
    {
        var trainerRemoved = roles.Contains(AppRoles.Trainer);
        var nutritionistRemoved = roles.Contains(AppRoles.Nutritionist);
        List<Guid> clientUserIds;

        try
        {
            clientUserIds = await db.ClientProfessionalLinks
                .AsNoTracking()
                .Where(l => l.ProfessionalProfileId == professionalProfileId
                            && l.ConnectionState != ConnectionState.Ended
                            && ((trainerRemoved && l.CanViewTrainingPlans) || (nutritionistRemoved && l.CanViewNutritionPlans)))
                .Select(l => l.ClientProfile.UserId)
                .Distinct()
                .ToListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "CoachRoleRemover: failed to load clients to notify for coach {CoachUserId}.", coach.Id);
            return;
        }

        var parameters = new Dictionary<string, string> { ["coachName"] = $"{coach.FirstName} {coach.LastName}" };

        foreach (var clientUserId in clientUserIds)
        {
            try
            {
                var notification = await notificationService.CreateAsync(
                    clientUserId, NotificationType.CoachRoleRemoved, parameters, ct: ct);

                await notifier.NotifyAsync(
                    clientUserId,
                    "newnotification",
                    new
                    {
                        id = notification.Id,
                        type = NotificationType.CoachRoleRemoved.ToString(),
                        data = notification.Data,
                    },
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "CoachRoleRemover: failed to notify client {ClientUserId} about coach {CoachUserId}.",
                    clientUserId, coach.Id);
            }
        }
    }
}
