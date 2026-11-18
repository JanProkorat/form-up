namespace FormUp.Application.Seed.WalkSeed;

/// <summary>
/// The fixed set of walk accounts.
/// </summary>
internal static class WalkAccountCatalog
{
    /// <summary>Email domain every walk account uses.</summary>
    public const string EmailDomain = "formup.test";

    /// <summary>Number of clients linked to the many-clients coach; one more than the Free plan allows.</summary>
    public const int ManyClientsClientCount = 3;

    /// <summary>Every walk account, admin first.</summary>
    public static readonly IReadOnlyList<WalkAccount> All =
    [
        Create("admin", WalkAccountKind.Admin, "Admin, no plan"),
        Create("free", WalkAccountKind.Free, "Coach on the Free plan, one role"),
        Create("pro", WalkAccountKind.Pro, "Pro, Stripe sandbox, card without 3D Secure"),
        Create("pro3ds", WalkAccountKind.ProThreeDSecure, "Pro, Stripe sandbox, card that needs 3D Secure on every later payment"),
        Create("given", WalkAccountKind.Given, "Pro plan given by Form Up, no Stripe subscription"),
        Create("manyclients", WalkAccountKind.ManyClients, "Pro, Stripe sandbox, 3 linked clients (Free allows 2)"),
    ];

    /// <summary>Email of the n-th (1-based) client of the many-clients coach.</summary>
    public static string ManyClientsClientEmail(int number) => $"walk.manyclients.client{number}@{EmailDomain}";

    /// <summary>Every email the walk seed owns, coaches and admin first, then the clients.</summary>
    public static IReadOnlyList<string> AllEmails() =>
    [
        .. All.Select(account => account.Email),
        .. Enumerable.Range(1, ManyClientsClientCount).Select(ManyClientsClientEmail),
    ];

    private static WalkAccount Create(string key, WalkAccountKind kind, string note) =>
        new(key, kind, $"walk.{key}@{EmailDomain}", note);
}
