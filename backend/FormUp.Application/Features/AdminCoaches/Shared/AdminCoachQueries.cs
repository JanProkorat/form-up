using System.Linq.Expressions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.AdminCoaches.Shared;

/// <summary>
/// Reads shared by the admin coaches list and detail: the per-coach projection, the Free plan, the client counts
/// (one grouped query) and the held coach roles (one query), so neither endpoint loops over coaches.
/// </summary>
internal static class AdminCoachQueries
{
    /// <summary>The columns of one coach, read in one query (a missing subscription row yields nulls).</summary>
    public static Expression<Func<ProfessionalProfile, AdminCoachRecord>> Project { get; } = profile => new AdminCoachRecord
    {
        CoachId = profile.UserId,
        ProfessionalProfileId = profile.Id,
        FirstName = profile.User.FirstName,
        LastName = profile.User.LastName,
        Email = profile.User.Email,
        JoinedAt = profile.DateCreated,
        TrainerRoleRemovedAt = profile.TrainerRoleRemovedAt,
        NutritionistRoleRemovedAt = profile.NutritionistRoleRemovedAt,
        Status = (SubscriptionStatus?)profile.CoachSubscription!.Status,
        FreeUntil = profile.CoachSubscription!.FreeUntil,
        GivenFreeAt = profile.CoachSubscription!.GivenFreeAt,
        CurrentPeriodEndsAt = profile.CoachSubscription!.CurrentPeriodEndsAt,
        CancelAtPeriodEnd = profile.CoachSubscription != null && profile.CoachSubscription.CancelAtPeriodEnd,
        PaysSecondRoleAddOn = profile.CoachSubscription != null && profile.CoachSubscription.PaysSecondRoleAddOn,
        PlanCode = profile.CoachSubscription!.SubscriptionPlan.Code,
        PlanNameCs = profile.CoachSubscription!.SubscriptionPlan.NameCs,
        PlanNameEn = profile.CoachSubscription!.SubscriptionPlan.NameEn,
        PlanNameDe = profile.CoachSubscription!.SubscriptionPlan.NameDe,
        PlanMaxActiveClients = profile.CoachSubscription!.SubscriptionPlan.MaxActiveClients,
        PlanBothRolesIncluded = profile.CoachSubscription != null && profile.CoachSubscription.SubscriptionPlan.BothRolesIncluded,
        PlanBillingInterval = (BillingInterval?)profile.CoachSubscription!.SubscriptionPlan.BillingInterval,
        PriceMinorUnits = (long?)profile.CoachSubscription!.SubscriptionPlanPrice.PriceMinorUnits,
        AddOnMinorUnits = profile.CoachSubscription!.SubscriptionPlanPrice.SecondRoleAddOnPriceMinorUnits,
        Currency = profile.CoachSubscription!.SubscriptionPlanPrice.Currency,
    };

    /// <summary>
    /// The Free plan's names and client cap. Null when the row is missing, which callers treat as a cap of zero.
    /// </summary>
    public static async Task<FreePlan?> LoadFreePlanAsync(IApplicationDbContext db, CancellationToken ct) =>
        await db.SubscriptionPlans
            .AsNoTracking()
            .Where(plan => plan.Code == SubscriptionPlanCodes.Free)
            .Select(plan => new FreePlan
            {
                Plan = new AdminCoachPlanDto { Code = plan.Code, NameCs = plan.NameCs, NameEn = plan.NameEn, NameDe = plan.NameDe },
                MaxActiveClients = plan.MaxActiveClients,
            })
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Counts each professional's active clients (distinct client profiles with a link that is not Ended) in one
    /// grouped query. Professionals with no clients are absent from the result.
    /// </summary>
    public static async Task<Dictionary<long, int>> CountClientsAsync(
        IApplicationDbContext db, IReadOnlyCollection<long> professionalProfileIds, CancellationToken ct) =>
        await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(link => professionalProfileIds.Contains(link.ProfessionalProfileId)
                           && link.ConnectionState != ConnectionState.Ended)
            .GroupBy(link => link.ProfessionalProfileId)
            .Select(group => new { ProfessionalProfileId = group.Key, Count = group.Select(link => link.ClientProfileId).Distinct().Count() })
            .ToDictionaryAsync(row => row.ProfessionalProfileId, row => row.Count, ct);

    /// <summary>
    /// The coach roles (Trainer, Nutritionist) each user holds in Identity, in one query.
    /// </summary>
    public static async Task<Dictionary<Guid, List<string>>> LoadHeldRolesAsync(
        IApplicationDbContext db, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        var rows = await db.UserRoles
            .AsNoTracking()
            .Where(userRole => userIds.Contains(userRole.UserId))
            .Join(db.Roles, userRole => userRole.RoleId, role => role.Id, (userRole, role) => new { userRole.UserId, role.Name })
            .Where(row => row.Name == AppRoles.Trainer || row.Name == AppRoles.Nutritionist)
            .ToListAsync(ct);

        return rows
            .GroupBy(row => row.UserId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.Name!).ToList());
    }

    /// <summary>
    /// The held coach roles not marked removed on the profile, Trainer before Nutritionist.
    /// </summary>
    public static List<string> ActiveRoles(AdminCoachRecord coach, IReadOnlyCollection<string>? heldRoles)
    {
        var roles = new List<string>();

        if (heldRoles?.Contains(AppRoles.Trainer) == true && coach.TrainerRoleRemovedAt is null)
        {
            roles.Add(AppRoles.Trainer);
        }

        if (heldRoles?.Contains(AppRoles.Nutritionist) == true && coach.NutritionistRoleRemovedAt is null)
        {
            roles.Add(AppRoles.Nutritionist);
        }

        return roles;
    }

    /// <summary>
    /// The plan whose limits apply to the coach right now: the assigned plan while it applies, otherwise the Free plan
    /// (cap zero when the Free plan row is missing).
    /// </summary>
    public static (AdminCoachPlanDto Plan, int? MaxActiveClients) ResolveEffectivePlan(
        AdminCoachRecord coach, FreePlan? free, DateTimeOffset now)
    {
        if (coach.Status is { } status
            && coach.PlanCode is not null
            && CoachSubscriptionStatusClassifier.PlanApplies(status, coach.FreeUntil, coach.GivenFreeAt, now))
        {
            return (
                new AdminCoachPlanDto
                {
                    Code = coach.PlanCode,
                    NameCs = coach.PlanNameCs ?? string.Empty,
                    NameEn = coach.PlanNameEn ?? string.Empty,
                    NameDe = coach.PlanNameDe ?? string.Empty,
                },
                coach.PlanMaxActiveClients);
        }

        return free is null
            ? (new AdminCoachPlanDto { Code = SubscriptionPlanCodes.Free }, 0)
            : (free.Plan, free.MaxActiveClients);
    }
}
