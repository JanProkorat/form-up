using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// The rules that decide whether a package, with an optional discount, may be given to a client. Shared by
/// setting a client's terms, creating an invite and accepting one, so all three answer alike.
/// </summary>
public static class PackageTermsRules
{
    /// <summary>
    /// Checks the rules in the order the endpoints answer them.
    /// </summary>
    /// <param name="entitled">Whether the coach's subscription includes both packages and client payments.</param>
    /// <param name="package">The package loaded for this coach, or null when unknown or foreign.</param>
    /// <param name="discountRequested">Whether a discount was asked for.</param>
    /// <param name="discount">The discount loaded for this coach with its package links, or null when unknown or foreign.</param>
    public static PackageTermsFailure Check(
        bool entitled, CoachPackage? package, bool discountRequested, CoachDiscount? discount)
    {
        if (!entitled)
        {
            return PackageTermsFailure.NotEntitled;
        }

        if (package is null)
        {
            return PackageTermsFailure.PackageNotFound;
        }

        if (package.ArchivedAt is not null)
        {
            return PackageTermsFailure.PackageArchived;
        }

        if (!discountRequested)
        {
            return PackageTermsFailure.None;
        }

        if (discount is null)
        {
            return PackageTermsFailure.DiscountNotFound;
        }

        if (discount.ArchivedAt is not null)
        {
            return PackageTermsFailure.DiscountArchived;
        }

        if (!CoachDiscountRules.CoversPackage(discount, package))
        {
            return PackageTermsFailure.DiscountPackageInvalid;
        }

        return discount.Type == DiscountType.FixedAmount && discount.Currency != package.Currency
            ? PackageTermsFailure.CurrencyMismatch
            : PackageTermsFailure.None;
    }
}
