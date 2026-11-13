using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.CoachSubscriptions.ChooseFreePlan;

/// <summary>
/// Moves a coach whose admin-given plan ended onto the Free plan, keeping the clients they choose when they have
/// more live clients than the Free plan's cap.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="timeProvider">Clock used to tell that the given plan ended.</param>
/// <param name="audit">Audit logging service.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
internal sealed class ChooseFreePlanEndpoint(IApplicationDbContext db, TimeProvider timeProvider, IAuditService audit)
    : Endpoint<ChooseFreePlanRequest>
{
    /// <summary>Audit action of choosing the Free plan.</summary>
    public const string AuditAction = "ChooseFreePlan";

    /// <inheritdoc />
    public override void Configure()
    {
        Post("/users/me/subscription/choose-free");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Choose the Free plan after a given plan ended";
            s.Description = "Only while the coach's admin-given plan has ended and no plan was chosen yet. Puts the coach on the Free " +
                            "plan's current price, clears the given-plan marker and, when the coach has more live clients than the Free " +
                            "cap, keeps exactly the listed clients and releases the rest.";
            s.Response(StatusCodes.Status204NoContent, "The Free plan was chosen");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,
                "Invalid body, a duplicate id, or the wrong number of clients to keep (KEEP_CLIENTS_COUNT_INVALID)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The caller is not a trainer or nutritionist, or the coach role was removed");
            s.Response(StatusCodes.Status404NotFound,
                "The caller has no professional profile, the Free plan is missing, or an id is not one of the caller's live clients");
            s.Response(StatusCodes.Status409Conflict, "No ended given plan is waiting for a choice (PLAN_CHOICE_NOT_REQUIRED)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(ChooseFreePlanRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(AppClaims.UserId), out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var profileId = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => (long?)p.Id)
            .FirstOrDefaultAsync(ct);

        if (profileId is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var subscription = await db.CoachSubscriptions
            .FirstOrDefaultAsync(cs => cs.ProfessionalProfileId == profileId, ct);

        if (subscription is null
            || !CoachSubscriptionStatusClassifier.IsPlanChoiceRequired(
                subscription.GivenFreeAt, subscription.FreeUntil, timeProvider.GetUtcNow()))
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict, ErrorCodes.PlanChoiceNotRequired, "There is no ended plan to replace.", ct);
            return;
        }

        var freePrice = await db.SubscriptionPlanPrices
            .AsNoTracking()
            .Where(p => p.SubscriptionPlan.Code == SubscriptionPlanCodes.Free && p.ValidTo == null)
            .Select(p => new { PriceId = p.Id, p.SubscriptionPlanId, p.SubscriptionPlan.MaxActiveClients })
            .FirstOrDefaultAsync(ct);

        if (freePrice is null)
        {
            await this.SendProblemAsync(
                StatusCodes.Status404NotFound, ErrorCodes.SubscriptionPlanNotFound, "Subscription plan not found.", ct);
            return;
        }

        var links = await db.ClientProfessionalLinks
            .Include(link => link.ClientProfile)
            .Where(link => link.ProfessionalProfileId == profileId && link.ConnectionState != ConnectionState.Ended)
            .ToListAsync(ct);

        var liveClientCount = links.Select(link => link.ClientProfileId).Distinct().Count();
        var keptCount = 0;
        var releasedCount = 0;

        if (freePrice.MaxActiveClients is { } cap && liveClientCount > cap)
        {
            var keepIds = req.KeepClientIds ?? [];

            if (keepIds.Distinct().Count() != keepIds.Count || keepIds.Count != cap)
            {
                this.ThrowErrorWithCode(
                    ErrorCodes.KeepClientsCountInvalid,
                    $"Choose exactly {cap} distinct clients to keep.");
                return;
            }

            var keepSet = keepIds.ToHashSet();

            if (!keepSet.IsSubsetOf(links.Select(link => link.ClientProfile.PublicId)))
            {
                await Send.NotFoundAsync(ct);
                return;
            }

            foreach (var link in links)
            {
                link.ConnectionState = keepSet.Contains(link.ClientProfile.PublicId)
                    ? ConnectionState.FreePlanKept
                    : ConnectionState.FreePlanReleased;
            }

            keptCount = cap;
            releasedCount = liveClientCount - cap;
        }

        subscription.SubscriptionPlanId = freePrice.SubscriptionPlanId;
        subscription.SubscriptionPlanPriceId = freePrice.PriceId;
        subscription.PendingSubscriptionPlanPriceId = null;
        subscription.Status = SubscriptionStatus.Active;
        subscription.FreeUntil = null;
        subscription.GivenFreeAt = null;
        subscription.PaysSecondRoleAddOn = false;

        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            userId,
            AuditAction,
            nameof(ApplicationUser),
            userId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            newValues: AuditPayload.WithReason(
                new Dictionary<string, object?>
                {
                    ["planCode"] = SubscriptionPlanCodes.Free,
                    ["keptClientCount"] = keptCount,
                    ["releasedClientCount"] = releasedCount,
                },
                reason: null),
            ct: ct);

        await Send.NoContentAsync(ct);
    }
}
