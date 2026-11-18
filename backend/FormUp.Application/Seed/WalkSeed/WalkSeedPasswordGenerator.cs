using System.Security.Cryptography;

namespace FormUp.Application.Seed.WalkSeed;

/// <summary>
/// Generates the throwaway passwords of the walk accounts.
/// </summary>
internal static class WalkSeedPasswordGenerator
{
    private const int Length = 28;
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Digits = "23456789";
    private const string Symbols = "-_.!";

    /// <summary>
    /// A random password with at least one lowercase letter, uppercase letter, digit and symbol. The symbols are safe in
    /// JSON and in a shell prompt.
    /// </summary>
    public static string Generate()
    {
        const string all = Lower + Upper + Digits + Symbols;

        var chars = new char[Length];
        chars[0] = Pick(Lower);
        chars[1] = Pick(Upper);
        chars[2] = Pick(Digits);
        chars[3] = Pick(Symbols);

        for (var i = 4; i < chars.Length; i++)
        {
            chars[i] = Pick(all);
        }

        RandomNumberGenerator.Shuffle(chars.AsSpan());

        return new string(chars);
    }

    private static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
}
