using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Client.Invites.Shared;

/// <summary>
/// Builds the package shown on a client's invite. A package is shown only when accepting the invite would still
/// give it: the coach is entitled to payments and neither the package nor the discount is archived or mismatched.
/// </summary>
internal static class InvitePackageLoader
{
    /// <summary>
    /// Loads the usable package of each invite that carries one.
    /// </summary>
    /// <param name="db">Application database context.</param>
    /// <param name="entitlementService">Resolves subscription entitlements.</param>
    /// <param name="invites">The invites; their package and discount ids are read.</param>
    /// <param name="now">The current instant (UTC).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The package by invite internal id; invites without a usable package are absent.</returns>
    public static async Task<Dictionary<long, InvitePackageDto>> LoadAsync(
        IApplicationDbContext db,
        EntitlementService entitlementService,
        IReadOnlyCollection<PendingInvite> invites,
        DateTime now,
        CancellationToken ct)
    {
        var withPackage = invites.Where(invite => invite.CoachPackageId is not null).ToList();

        if (withPackage.Count == 0)
        {
            return [];
        }

        var coachIds = withPackage.Select(invite => invite.ProfessionalProfileId).Distinct().ToList();
        var entitledIds = await entitlementService.GetEntitledProfileIdsAsync(
            coachIds, entitlements => entitlements.CanUsePackages && entitlements.CanUsePayments, ct);

        var packageIds = withPackage.Select(invite => invite.CoachPackageId!.Value).Distinct().ToList();
        var packages = await db.CoachPackages
            .AsNoTracking()
            .Where(package => packageIds.Contains(package.Id))
            .ToDictionaryAsync(package => package.Id, ct);

        var discountIds = withPackage
            .Where(invite => invite.CoachDiscountId is not null)
            .Select(invite => invite.CoachDiscountId!.Value)
            .Distinct()
            .ToList();
        var discounts = await db.CoachDiscounts
            .AsNoTracking()
            .Include(discount => discount.Packages)
            .Where(discount => discountIds.Contains(discount.Id))
            .ToDictionaryAsync(discount => discount.Id, ct);

        var today = ClientPaymentSchedule.Today(now);
        var result = new Dictionary<long, InvitePackageDto>();

        foreach (var invite in withPackage)
        {
            var package = packages.GetValueOrDefault(invite.CoachPackageId!.Value);
            var discount = invite.CoachDiscountId is { } discountId ? discounts.GetValueOrDefault(discountId) : null;

            // A package or discount of another coach is treated as missing, like the accept does.
            if (package?.ProfessionalProfileId != invite.ProfessionalProfileId
                || (discount is not null && discount.ProfessionalProfileId != invite.ProfessionalProfileId))
            {
                continue;
            }

            var failure = PackageTermsRules.Check(
                entitledIds.Contains(invite.ProfessionalProfileId), package, invite.CoachDiscountId is not null, discount);

            if (failure != PackageTermsFailure.None)
            {
                continue;
            }

            var applied = discount is not null && CoachDiscountRules.Applies(discount, 1, today);

            result[invite.Id] = new InvitePackageDto
            {
                Name = package.Name,
                Billing = package.Billing,
                IntervalWeeks = package.IntervalWeeks,
                RunsForPayments = package.RunsForPayments,
                Description = package.Description,
                BasePrice = package.Price,
                DiscountLabel = applied ? ClientPaymentSchedule.DiscountLabel(discount!) : null,
                FinalPrice = applied ? CoachDiscountRules.Apply(discount!, package.Price) : package.Price,
                Currency = package.Currency
            };
        }

        return result;
    }
}
