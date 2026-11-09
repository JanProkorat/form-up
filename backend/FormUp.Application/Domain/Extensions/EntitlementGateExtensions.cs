using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FormUp.Application.Domain.Extensions;

/// <summary>
/// Shared 403 responses for endpoints that a coach's subscription gates. Each method asks
/// <see cref="EntitlementService"/> and, when the answer is no, writes the coded response and returns
/// true — the caller returns immediately.
/// </summary>
public static class EntitlementGateExtensions
{
    /// <summary>
    /// Answers 403 <see cref="ErrorCodes.SubscriptionRequired"/> when the caller's subscription lacks the feature.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="db">Application database context.</param>
    /// <param name="userId">The caller's <c>ApplicationUser.Id</c>.</param>
    /// <param name="feature">Reads the feature flag to require from the resolved entitlements.</param>
    /// <param name="detail">Human-readable explanation.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<bool> RespondIfFeatureNotEntitledAsync(
        this IEndpoint endpoint,
        IApplicationDbContext db,
        Guid userId,
        Func<CoachEntitlements, bool> feature,
        string detail,
        CancellationToken ct)
    {
        var entitlements = await endpoint.ResolveEntitlementService(db).GetEntitlementsForUserAsync(userId, ct);

        return await endpoint.RespondIfNotEntitledAsync(feature(entitlements), detail, ct);
    }

    /// <summary>
    /// Answers 403 <see cref="ErrorCodes.SubscriptionRequired"/> when the professional's subscription lacks the feature.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="db">Application database context.</param>
    /// <param name="professionalProfileId">The professional's <c>ProfessionalProfile.Id</c>.</param>
    /// <param name="feature">Reads the feature flag to require from the resolved entitlements.</param>
    /// <param name="detail">Human-readable explanation.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<bool> RespondIfFeatureNotEntitledAsync(
        this IEndpoint endpoint,
        IApplicationDbContext db,
        long professionalProfileId,
        Func<CoachEntitlements, bool> feature,
        string detail,
        CancellationToken ct)
    {
        var entitlements = await endpoint.ResolveEntitlementService(db)
            .GetEntitlementsAsync(professionalProfileId, ct);

        return await endpoint.RespondIfNotEntitledAsync(feature(entitlements), detail, ct);
    }

    /// <summary>
    /// Answers 403 <see cref="ErrorCodes.SubscriptionRequired"/> when the user may not hold a second professional role.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="db">Application database context.</param>
    /// <param name="userId">The caller's <c>ApplicationUser.Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<bool> RespondIfSecondRoleNotAvailableAsync(
        this IEndpoint endpoint, IApplicationDbContext db, Guid userId, CancellationToken ct)
    {
        var state = await endpoint.ResolveEntitlementService(db).GetSecondRoleStateForUserAsync(userId, ct);

        return await endpoint.RespondIfNotEntitledAsync(
            state != SecondRoleState.NotAvailable,
            "The current subscription does not include a second professional role.",
            ct);
    }

    /// <summary>
    /// Answers 403 <see cref="ErrorCodes.ClientLimitReached"/> when the professional is at the active-client
    /// limit and the client would take a new slot. A client who already has an active link to the professional
    /// takes none. Call it inside the write's transaction, after locking the client's profile row.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="db">Application database context.</param>
    /// <param name="professionalProfileId">The professional's <c>ProfessionalProfile.Id</c>.</param>
    /// <param name="clientProfileId">The client's profile id, or null when the client is not known yet.</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task<bool> RespondIfClientLimitReachedAsync(
        this IEndpoint endpoint,
        IApplicationDbContext db,
        long professionalProfileId,
        long? clientProfileId,
        CancellationToken ct) =>
        endpoint.RespondIfClientLimitReachedAsync(
            endpoint.ResolveEntitlementService(db), professionalProfileId, clientProfileId, ct);

    /// <summary>
    /// Same as the overload taking a database context, for an endpoint that already holds an <see cref="EntitlementService"/>.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="entitlementService">Resolves subscription entitlements.</param>
    /// <param name="professionalProfileId">The professional's <c>ProfessionalProfile.Id</c>.</param>
    /// <param name="clientProfileId">The client's profile id, or null when the client is not known yet.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<bool> RespondIfClientLimitReachedAsync(
        this IEndpoint endpoint,
        EntitlementService entitlementService,
        long professionalProfileId,
        long? clientProfileId,
        CancellationToken ct)
    {
        if (await entitlementService.HasCapacityForClientAsync(professionalProfileId, clientProfileId, ct))
        {
            return false;
        }

        await endpoint.SendProblemAsync(
            StatusCodes.Status403Forbidden,
            ErrorCodes.ClientLimitReached,
            "The current subscription's active-client limit is reached.",
            ct);
        return true;
    }

    /// <summary>
    /// Answers 403 <see cref="ErrorCodes.ClientOverPlanLimit"/> when the coach's paid plan has lapsed and the
    /// client is over the Free plan's cap. For actions that are always refused for such a client.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="db">Application database context.</param>
    /// <param name="coachUserId">The caller's <c>ApplicationUser.Id</c>.</param>
    /// <param name="clientUserId">The client's <c>ApplicationUser.Id</c> (what Mongo plans store as ClientId).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<bool> RespondIfClientOverPlanLimitAsync(
        this IEndpoint endpoint, IApplicationDbContext db, Guid coachUserId, Guid clientUserId, CancellationToken ct)
    {
        var overCap = await endpoint.ResolveEntitlementService(db).IsClientOverCapAsync(coachUserId, clientUserId, ct);

        return overCap && await endpoint.RespondClientOverPlanLimitAsync(ct);
    }

    /// <summary>
    /// Same as the overload taking the client's user id, for a caller that holds the client's profile id.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="db">Application database context.</param>
    /// <param name="coachUserId">The caller's <c>ApplicationUser.Id</c>.</param>
    /// <param name="clientProfileId">The client's <c>ClientProfile.Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<bool> RespondIfClientOverPlanLimitAsync(
        this IEndpoint endpoint, IApplicationDbContext db, Guid coachUserId, long clientProfileId, CancellationToken ct)
    {
        var overCap = await endpoint.ResolveEntitlementService(db).IsClientOverCapAsync(coachUserId, clientProfileId, ct);

        return overCap && await endpoint.RespondClientOverPlanLimitAsync(ct);
    }

    /// <summary>
    /// For actions on an existing plan that stay allowed until its last published week has ended. Answers
    /// 403 <see cref="ErrorCodes.ClientOverPlanLimit"/> when the client is over the Free plan's cap and
    /// <paramref name="runsUntil"/> is null (no published week) or already past.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="db">Application database context.</param>
    /// <param name="coachUserId">The caller's <c>ApplicationUser.Id</c>.</param>
    /// <param name="clientUserId">The client's <c>ApplicationUser.Id</c> (what Mongo plans store as ClientId).</param>
    /// <param name="runsUntil">Last day of the plan's last published week, or null when none is published.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<OverCapEditAccess> RespondIfClientOverPlanLimitAfterRunsUntilAsync(
        this IEndpoint endpoint,
        IApplicationDbContext db,
        Guid coachUserId,
        Guid clientUserId,
        DateOnly? runsUntil,
        CancellationToken ct)
    {
        if (!await endpoint.ResolveEntitlementService(db).IsClientOverCapAsync(coachUserId, clientUserId, ct))
        {
            return OverCapEditAccess.Unrestricted;
        }

        var today = DateOnly.FromDateTime(endpoint.ResolveTimeProvider().GetUtcNow().UtcDateTime);

        if (runsUntil is { } lastDay && today <= lastDay)
        {
            return OverCapEditAccess.WindowOpen;
        }

        await endpoint.RespondClientOverPlanLimitAsync(ct);
        return OverCapEditAccess.Refused;
    }

    /// <summary>
    /// The last day a plan's published weeks cover, for a client who is over the lapsed coach's Free plan cap; null
    /// when the client is not over the cap (or no week is published), so the client app shows no end-of-plan notice.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="db">Application database context.</param>
    /// <param name="coachUserId">The plan author's <c>ApplicationUser.Id</c>.</param>
    /// <param name="clientUserId">The client's <c>ApplicationUser.Id</c> (what Mongo plans store as ClientId).</param>
    /// <param name="startDate">The plan's start date.</param>
    /// <param name="publishedWeekNumbers">Week numbers whose own status is Published.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<DateOnly?> ResolveRunsUntilForOverCapClientAsync(
        this IEndpoint endpoint,
        IApplicationDbContext db,
        Guid coachUserId,
        Guid clientUserId,
        DateTime? startDate,
        IEnumerable<int> publishedWeekNumbers,
        CancellationToken ct) =>
        await endpoint.ResolveEntitlementService(db).IsClientOverCapAsync(coachUserId, clientUserId, ct)
            ? PlanRunsUntilResolver.Resolve(startDate, publishedWeekNumbers)
            : null;

    private static async Task<bool> RespondClientOverPlanLimitAsync(this IEndpoint endpoint, CancellationToken ct)
    {
        await endpoint.SendProblemAsync(
            StatusCodes.Status403Forbidden,
            ErrorCodes.ClientOverPlanLimit,
            "The client is over the Free plan's client limit, so this action is not available.",
            ct);
        return true;
    }

    private static TimeProvider ResolveTimeProvider(this IEndpoint endpoint) =>
        endpoint.HttpContext.RequestServices?.GetService<TimeProvider>() ?? TimeProvider.System;

    private static async Task<bool> RespondIfNotEntitledAsync(
        this IEndpoint endpoint, bool entitled, string detail, CancellationToken ct)
    {
        if (entitled)
        {
            return false;
        }

        await endpoint.SendProblemAsync(StatusCodes.Status403Forbidden, ErrorCodes.SubscriptionRequired, detail, ct);
        return true;
    }

    /// <summary>
    /// The request's scoped <see cref="EntitlementService"/> (clock and logger wired). Only when the request has
    /// no such service — an endpoint unit test built without a host — is one built over <paramref name="db"/>.
    /// </summary>
    private static EntitlementService ResolveEntitlementService(this IEndpoint endpoint, IApplicationDbContext db) =>
        endpoint.HttpContext.RequestServices?.GetService<EntitlementService>()
        ?? new EntitlementService(db, TimeProvider.System, NullLogger<EntitlementService>.Instance);
}
