using System.Text.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Gives a client the package an invite carried, right after they accepted it. The terms are checked again
/// here because the coach may have archived the package or lost payments since sending the invite; when they
/// no longer hold, the invite is still accepted, just without terms.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="entitlementService">Resolves subscription entitlements.</param>
/// <param name="termsWriter">Stores the terms and creates the billing periods.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="logger">Logger.</param>
public sealed class InvitePackageTermsApplier(
    IApplicationDbContext db,
    EntitlementService entitlementService,
    ClientPaymentTermsWriter termsWriter,
    IAuditService audit,
    ILogger<InvitePackageTermsApplier> logger)
{
    /// <summary>Audit action recorded when the invite's terms could not be applied.</summary>
    public const string SkippedAuditAction = "InvitePackageTermsSkipped";

    /// <summary>
    /// Applies the invite's package (and discount) to the client, with billing starting today. Never throws for a
    /// business failure: the reason is logged and audited and the method returns false.
    /// </summary>
    /// <param name="invitePublicId">The invite's public id, the audit entry's subject.</param>
    /// <param name="professionalProfileId">The coach's internal id.</param>
    /// <param name="packageId">The invite's package, internal id.</param>
    /// <param name="discountId">The invite's discount, internal id, or null.</param>
    /// <param name="clientProfileId">The client's internal id.</param>
    /// <param name="clientUserId">The accepting user.</param>
    /// <param name="ipAddress">The caller's IP address.</param>
    /// <param name="now">The current instant (UTC).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="subjectEntity">Audit entity name of the subject; defaults to the pending invite.</param>
    /// <returns>True when the client now has the terms.</returns>
    public async Task<bool> ApplyAsync(
        Guid invitePublicId,
        long professionalProfileId,
        long packageId,
        long? discountId,
        long clientProfileId,
        Guid clientUserId,
        string? ipAddress,
        DateTime now,
        CancellationToken ct,
        string subjectEntity = nameof(PendingInvite))
    {
        try
        {
            var entitlements = await entitlementService.GetEntitlementsAsync(professionalProfileId, ct);

            var package = await db.CoachPackages
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == packageId && p.ProfessionalProfileId == professionalProfileId, ct);

            var discount = discountId is null
                ? null
                : await db.CoachDiscounts
                    .AsNoTracking()
                    .Include(d => d.Packages)
                    .FirstOrDefaultAsync(d => d.Id == discountId && d.ProfessionalProfileId == professionalProfileId, ct);

            var failure = PackageTermsRules.Check(entitlements.CanUsePackages && entitlements.CanUsePayments, package, discountId is not null, discount);

            if (failure != PackageTermsFailure.None)
            {
                await SkipAsync(invitePublicId, failure.ToString(), clientUserId, ipAddress, subjectEntity, ct);
                return false;
            }

            var assignment = await termsWriter.StoreAsync(
                professionalProfileId, clientProfileId, packageId, discountId, null, null, clientUserId, ipAddress, now, ct);

            if (assignment is null)
            {
                await SkipAsync(invitePublicId, "ClientNumberExhausted", clientUserId, ipAddress, subjectEntity, ct);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The link is already committed; a failure here must not turn the accept into an error.
            logger.LogError(ex, "Applying the terms of invite {InvitePublicId} failed after it was accepted", invitePublicId);
            return false;
        }
    }

    private async Task SkipAsync(
        Guid invitePublicId, string reason, Guid clientUserId, string? ipAddress, string subjectEntity, CancellationToken ct)
    {
        logger.LogWarning(
            "Invite {InvitePublicId} was accepted without its package terms: {Reason}", invitePublicId, reason);

        await audit.LogAsync(
            clientUserId,
            SkippedAuditAction,
            subjectEntity,
            invitePublicId,
            ipAddress,
            null,
            JsonSerializer.Serialize(new { reason }),
            ct);
    }
}
