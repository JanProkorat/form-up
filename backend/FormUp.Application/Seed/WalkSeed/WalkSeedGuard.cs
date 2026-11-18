namespace FormUp.Application.Seed.WalkSeed;

/// <summary>
/// The safety checks that run before the walk seed touches anything.
/// </summary>
internal static class WalkSeedGuard
{
    /// <summary>Prefix of a Stripe test-mode secret key.</summary>
    public const string TestKeyPrefix = "sk_test_";

    /// <summary>
    /// Returns why the seed must not run, or null when it may. The key itself is never part of a message.
    /// </summary>
    /// <param name="environmentName">The host environment name.</param>
    /// <param name="stripeSecretKey">The configured Stripe secret key.</param>
    public static string? Check(string environmentName, string? stripeSecretKey)
    {
        if (!string.Equals(environmentName, "Development", StringComparison.Ordinal))
        {
            return $"Refusing to run: the environment is '{environmentName}', and --walk-seed only runs in Development.";
        }

        if (string.IsNullOrWhiteSpace(stripeSecretKey))
        {
            return "Refusing to run: no Stripe secret key is configured (Stripe__SecretKey). Use a test-mode key.";
        }

        if (!stripeSecretKey.StartsWith(TestKeyPrefix, StringComparison.Ordinal))
        {
            return "Refusing to run: the Stripe secret key is not a test-mode key (it must start with sk_test_).";
        }

        return null;
    }
}
