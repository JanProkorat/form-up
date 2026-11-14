using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Domain.Extensions;

/// <summary>
/// The caller-is-a-coach-with-the-feature guard shared by every coach-facing payments and packages action.
/// </summary>
internal static class PaymentsAccessExtensions
{
    /// <summary>
    /// Resolves the caller's professional profile id and checks the subscription includes client payments.
    /// Writes a 404 (no professional profile) or a coded 403 (not entitled) and returns null when
    /// the caller may not proceed — the caller must return immediately on null.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="db">Application database context.</param>
    /// <param name="entitlementService">Resolves subscription entitlements.</param>
    /// <param name="userId">The caller's ApplicationUser.Id from JWT.</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task<long?> LoadEntitledProfileIdOrRespondAsync(
        this IEndpoint endpoint,
        IApplicationDbContext db,
        EntitlementService entitlementService,
        Guid userId,
        CancellationToken ct) =>
        endpoint.LoadEntitledProfileIdOrRespondAsync(
            db,
            entitlementService,
            userId,
            entitlements => entitlements.CanUsePayments,
            "The current subscription does not include payments.",
            ct);

    /// <summary>
    /// Same as <see cref="LoadEntitledProfileIdOrRespondAsync(IEndpoint, IApplicationDbContext, EntitlementService, Guid, CancellationToken)"/>
    /// but checks the subscription includes coach packages.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="db">Application database context.</param>
    /// <param name="entitlementService">Resolves subscription entitlements.</param>
    /// <param name="userId">The caller's ApplicationUser.Id from JWT.</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task<long?> LoadPackagesEntitledProfileIdOrRespondAsync(
        this IEndpoint endpoint,
        IApplicationDbContext db,
        EntitlementService entitlementService,
        Guid userId,
        CancellationToken ct) =>
        endpoint.LoadEntitledProfileIdOrRespondAsync(
            db,
            entitlementService,
            userId,
            entitlements => entitlements.CanUsePackages,
            "The current subscription does not include packages.",
            ct);

    private static async Task<long?> LoadEntitledProfileIdOrRespondAsync(
        this IEndpoint endpoint,
        IApplicationDbContext db,
        EntitlementService entitlementService,
        Guid userId,
        Func<CoachEntitlements, bool> feature,
        string detail,
        CancellationToken ct)
    {
        var profileId = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => (long?)p.Id)
            .FirstOrDefaultAsync(ct);

        if (profileId is null)
        {
            await endpoint.HttpContext.Response.SendNotFoundAsync(ct);
            return null;
        }

        var entitlements = await entitlementService.GetEntitlementsAsync(profileId.Value, ct);

        if (!feature(entitlements))
        {
            await endpoint.SendProblemAsync(
                StatusCodes.Status403Forbidden,
                ErrorCodes.PaymentsNotEntitled,
                detail,
                ct);
            return null;
        }

        return profileId;
    }
}
