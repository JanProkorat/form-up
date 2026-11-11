using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.AdminCoaches.Shared;

/// <summary>What an admin did to the coach's plan.</summary>
public enum CoachPlanChangeAction
{
    /// <summary>The plan was changed (given free).</summary>
    Changed,

    /// <summary>The plan was ended now.</summary>
    Ended,

    /// <summary>The renewal was cancelled.</summary>
    RenewalCancelled
}

/// <summary>
/// Tells a coach their plan was changed by an admin: an in-app notification plus the "subscriptionchanged" event.
/// Never throws; the plan change is already saved when this runs.
/// </summary>
internal static class CoachPlanNotifier
{
    /// <summary>
    /// Creates the notification and sends "newnotification" and "subscriptionchanged" to the coach only.
    /// A failure is logged as a warning and swallowed.
    /// </summary>
    /// <param name="db">Application database context.</param>
    /// <param name="notifications">Notification service.</param>
    /// <param name="notifier">Realtime notifier.</param>
    /// <param name="logger">Logger of the calling endpoint.</param>
    /// <param name="coachUserId">The coach's user id.</param>
    /// <param name="planId">Id of the plan the change is about.</param>
    /// <param name="action">What happened.</param>
    /// <param name="until">End of the plan, or null when it has none.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task TryNotifyAsync(
        IApplicationDbContext db,
        INotificationService notifications,
        IRealtimeNotifier notifier,
        ILogger logger,
        Guid coachUserId,
        long planId,
        CoachPlanChangeAction action,
        DateTimeOffset? until,
        CancellationToken ct)
    {
        try
        {
            var plan = await db.SubscriptionPlans
                .AsNoTracking()
                .Where(p => p.Id == planId)
                .Select(p => new { p.NameCs, p.NameEn, p.NameDe })
                .FirstAsync(ct);
            var language = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == coachUserId)
                .Select(u => u.Language)
                .FirstOrDefaultAsync(ct) ?? "en";

            var planName = language.ToLowerInvariant() switch
            {
                "cs" => plan.NameCs,
                "de" => plan.NameDe,
                _ => plan.NameEn,
            };

            var type = action switch
            {
                CoachPlanChangeAction.Changed => NotificationType.CoachPlanChanged,
                CoachPlanChangeAction.Ended => NotificationType.CoachPlanEnded,
                _ => NotificationType.CoachPlanRenewalCancelled,
            };

            var parameters = new Dictionary<string, string> { ["planName"] = planName };
            string? variant = null;

            if (action != CoachPlanChangeAction.Ended)
            {
                if (until is { } end)
                {
                    parameters["date"] = PaymentMessageComposer.FormatDate(DateOnly.FromDateTime(end.UtcDateTime), language);
                }
                else
                {
                    variant = NotificationTemplates.CoachPlanNoDate;
                }
            }

            var notification = await notifications.CreateAsync(coachUserId, type, parameters, variant, ct);

            await notifier.NotifyAsync(
                coachUserId,
                "newnotification",
                new { id = notification.Id, type = type.ToString(), data = notification.Data },
                ct);

            await notifier.NotifyAsync(
                coachUserId,
                "subscriptionchanged",
                new
                {
                    action = action switch
                    {
                        CoachPlanChangeAction.Changed => "changed",
                        CoachPlanChangeAction.Ended => "ended",
                        _ => "renewalcancelled",
                    },
                    planNameCs = plan.NameCs,
                    planNameEn = plan.NameEn,
                    planNameDe = plan.NameDe,
                    until,
                },
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Plan change notification failed for coach {CoachId} ({Action})", coachUserId, action);
        }
    }
}
