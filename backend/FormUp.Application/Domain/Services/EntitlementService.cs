using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Resolves a coach's effective feature entitlements, client-count limit and second-role state from
/// their <see cref="Entities.CoachSubscription"/> and its <see cref="Entities.SubscriptionPlan"/>.
/// </summary>
/// <remarks>
/// A professional with no subscription row gets the Free plan's entitlements. While the clock is before
/// <see cref="Entities.CoachSubscription.FreeUntil"/>, or while the status is Trialing, Active or PastDue,
/// the assigned plan's own flags apply; Canceled and Incomplete fall back to the Free plan. When the
/// Free plan row itself is missing the service logs an error and resolves to
/// <see cref="CoachEntitlements.Locked"/> (fail closed).
/// </remarks>
public class EntitlementService
{
    private readonly IApplicationDbContext db;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<EntitlementService> logger;

    /// <summary>
    /// Creates the service with the clock and logger it needs.
    /// </summary>
    /// <param name="db">Application database context.</param>
    /// <param name="timeProvider">Clock used to compare against <c>FreeUntil</c>.</param>
    /// <param name="logger">Logger for the missing-Free-plan error.</param>
    public EntitlementService(IApplicationDbContext db, TimeProvider timeProvider, ILogger<EntitlementService> logger)
    {
        this.db = db;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// Resolves the effective entitlements for a professional.
    /// </summary>
    /// <param name="professionalProfileId">The professional's <c>ProfessionalProfile.Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<CoachEntitlements> GetEntitlementsAsync(long professionalProfileId, CancellationToken ct) =>
        (await ResolveAsync(professionalProfileId, ct)).Entitlements;

    /// <summary>
    /// Resolves the effective entitlements for the professional profile of a user. A user with no
    /// professional profile resolves like a coach with no subscription row.
    /// </summary>
    /// <param name="userId">The caller's <c>ApplicationUser.Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<CoachEntitlements> GetEntitlementsForUserAsync(Guid userId, CancellationToken ct) =>
        (await ResolveAsync(await FindProfileIdAsync(userId, ct), ct)).Entitlements;

    /// <summary>
    /// Resolves whether a second professional role is available to the professional.
    /// </summary>
    /// <param name="professionalProfileId">The professional's <c>ProfessionalProfile.Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<SecondRoleState> GetSecondRoleStateAsync(long professionalProfileId, CancellationToken ct) =>
        ToSecondRoleState(await ResolveAsync(professionalProfileId, ct));

    /// <summary>
    /// Resolves whether a second professional role is available to the user.
    /// </summary>
    /// <param name="userId">The caller's <c>ApplicationUser.Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<SecondRoleState> GetSecondRoleStateForUserAsync(Guid userId, CancellationToken ct) =>
        ToSecondRoleState(await ResolveAsync(await FindProfileIdAsync(userId, ct), ct));

    /// <summary>
    /// Resolves, with the same rules as <see cref="GetEntitlementsAsync"/>, which of the given professionals
    /// may use the payments feature.
    /// </summary>
    /// <param name="professionalProfileIds">The professionals' <c>ProfessionalProfile.Id</c> values.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<HashSet<long>> GetPaymentsEntitledProfileIdsAsync(
        IReadOnlyCollection<long> professionalProfileIds, CancellationToken ct)
    {
        var rows = await db.CoachSubscriptions
            .AsNoTracking()
            .Where(cs => professionalProfileIds.Contains(cs.ProfessionalProfileId))
            .Select(ProjectRow())
            .ToListAsync(ct);

        var rowsByProfile = rows.ToDictionary(row => row.ProfessionalProfileId);
        var needsFree = professionalProfileIds.Any(id => !rowsByProfile.TryGetValue(id, out var row) || !PlanApplies(row));
        var free = needsFree ? await LoadFreePlanAsync(ct) : null;

        return professionalProfileIds
            .Where(id => Resolve(rowsByProfile.GetValueOrDefault(id), free).Entitlements.CanUsePayments)
            .ToHashSet();
    }

    /// <summary>
    /// Counts the professional's active clients: distinct client profiles across all their active links,
    /// so a client linked in both roles counts once. Independent of subscription state.
    /// </summary>
    /// <param name="professionalProfileId">The professional's <c>ProfessionalProfile.Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<int> GetActiveClientCountAsync(long professionalProfileId, CancellationToken ct) =>
        db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(cpl => cpl.ProfessionalProfileId == professionalProfileId && cpl.ConnectionState != ConnectionState.Ended)
            .Select(cpl => cpl.ClientProfileId)
            .Distinct()
            .CountAsync(ct);

    /// <summary>
    /// Whether the professional may take on another active client, given their current entitlements
    /// and distinct active client count.
    /// </summary>
    /// <param name="professionalProfileId">The professional's <c>ProfessionalProfile.Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> CanAddClientAsync(long professionalProfileId, CancellationToken ct)
    {
        var entitlements = await GetEntitlementsAsync(professionalProfileId, ct);

        if (entitlements.MaxActiveClients is null)
        {
            return true;
        }

        return await GetActiveClientCountAsync(professionalProfileId, ct) < entitlements.MaxActiveClients;
    }

    /// <summary>
    /// Whether the professional has room for the given client. A client who already has an active link to
    /// the professional takes no new slot, so adding a second role for them is never refused. Pass null
    /// when the client is not known yet (a new invite).
    /// </summary>
    /// <param name="professionalProfileId">The professional's <c>ProfessionalProfile.Id</c>.</param>
    /// <param name="clientProfileId">The client's profile id, or null when there is none yet.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> HasCapacityForClientAsync(
        long professionalProfileId, long? clientProfileId, CancellationToken ct)
    {
        if (clientProfileId is not null
            && await db.ClientProfessionalLinks
                .AsNoTracking()
                .AnyAsync(cpl => cpl.ProfessionalProfileId == professionalProfileId
                                 && cpl.ClientProfileId == clientProfileId
                                 && cpl.ConnectionState != ConnectionState.Ended, ct))
        {
            return true;
        }

        return await CanAddClientAsync(professionalProfileId, ct);
    }

    private async Task<long?> FindProfileIdAsync(Guid userId, CancellationToken ct) =>
        await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => (long?)p.Id)
            .FirstOrDefaultAsync(ct);

    private async Task<Resolution> ResolveAsync(long? professionalProfileId, CancellationToken ct)
    {
        SubscriptionRow? row = null;

        if (professionalProfileId is not null)
        {
            row = await db.CoachSubscriptions
                .AsNoTracking()
                .Where(cs => cs.ProfessionalProfileId == professionalProfileId)
                .Select(ProjectRow())
                .FirstOrDefaultAsync(ct);
        }

        var free = row is not null && PlanApplies(row) ? null : await LoadFreePlanAsync(ct);

        return Resolve(row, free);
    }

    private Resolution Resolve(SubscriptionRow? row, PlanFlags? free)
    {
        if (row is not null && PlanApplies(row))
        {
            return new Resolution(row.Plan.ToEntitlements(), row.Plan.BothRolesIncluded, row.PaysSecondRoleAddOn);
        }

        return free is null
            ? new Resolution(CoachEntitlements.Locked, false, false)
            : new Resolution(free.ToEntitlements(), free.BothRolesIncluded, false);
    }

    private bool PlanApplies(SubscriptionRow row) =>
        timeProvider.GetUtcNow() < row.FreeUntil
        || row.Status is SubscriptionStatus.Trialing or SubscriptionStatus.Active or SubscriptionStatus.PastDue;

    private async Task<PlanFlags?> LoadFreePlanAsync(CancellationToken ct)
    {
        var free = await db.SubscriptionPlans
            .AsNoTracking()
            .Where(p => p.Code == SubscriptionPlanCodes.Free)
            .Select(p => new PlanFlags(
                p.CanCreatePlans,
                p.CanMessage,
                p.CanSendQuestionnaires,
                p.CanUseWeeklyCheckIns,
                p.CanUsePerClientCheckInConfig,
                p.CanUsePayments,
                p.MaxActiveClients,
                p.BothRolesIncluded))
            .FirstOrDefaultAsync(ct);

        if (free is null)
        {
            logger.LogError(
                "The {PlanCode} subscription plan row is missing; coach entitlements fail closed",
                SubscriptionPlanCodes.Free);
        }

        return free;
    }

    private static SecondRoleState ToSecondRoleState(Resolution resolution)
    {
        if (resolution.BothRolesIncluded)
        {
            return SecondRoleState.Included;
        }

        return resolution.PaysSecondRoleAddOn ? SecondRoleState.PaidAddOn : SecondRoleState.NotAvailable;
    }

    private static System.Linq.Expressions.Expression<Func<Entities.CoachSubscription, SubscriptionRow>> ProjectRow() =>
        cs => new SubscriptionRow(
            cs.ProfessionalProfileId,
            cs.Status,
            cs.FreeUntil,
            cs.PaysSecondRoleAddOn,
            new PlanFlags(
                cs.SubscriptionPlan.CanCreatePlans,
                cs.SubscriptionPlan.CanMessage,
                cs.SubscriptionPlan.CanSendQuestionnaires,
                cs.SubscriptionPlan.CanUseWeeklyCheckIns,
                cs.SubscriptionPlan.CanUsePerClientCheckInConfig,
                cs.SubscriptionPlan.CanUsePayments,
                cs.SubscriptionPlan.MaxActiveClients,
                cs.SubscriptionPlan.BothRolesIncluded));

    private sealed record PlanFlags(
        bool CanCreatePlans,
        bool CanMessage,
        bool CanSendQuestionnaires,
        bool CanUseWeeklyCheckIns,
        bool CanUsePerClientCheckInConfig,
        bool CanUsePayments,
        int? MaxActiveClients,
        bool BothRolesIncluded)
    {
        public CoachEntitlements ToEntitlements() => new(
            CanCreatePlans,
            CanMessage,
            CanSendQuestionnaires,
            CanUseWeeklyCheckIns,
            CanUsePerClientCheckInConfig,
            CanUsePayments,
            MaxActiveClients);
    }

    private sealed record SubscriptionRow(
        long ProfessionalProfileId,
        SubscriptionStatus Status,
        DateTimeOffset? FreeUntil,
        bool PaysSecondRoleAddOn,
        PlanFlags Plan);

    private readonly record struct Resolution(
        CoachEntitlements Entitlements,
        bool BothRolesIncluded,
        bool PaysSecondRoleAddOn);
}
