using System.Security.Cryptography;

namespace Weir.Core.Auth;

/// <summary>
/// Recovery codes: ten single-use codes of four groups of five characters. The first group is stored in the clear to find the
/// code's row, so a typed code is checked against one hash and not ten; the rest of the code is what keeps it secret.
/// </summary>
public static class RecoveryCodeRules
{
    public const int SetSize = 10;

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int GroupLength = 5;
    private const int GroupCount = 4;

    /// <summary>One new code, such as <c>K7M2P-9XQ4T-HWB3D-RN6YC</c>.</summary>
    public static string Generate() => string.Join(
        '-',
        Enumerable.Range(0, GroupCount).Select(_ => new string(RandomNumberGenerator.GetItems<char>(Alphabet, GroupLength))));

    /// <summary>A set of <see cref="SetSize"/> codes whose lookups all differ. <paramref name="next"/> is the code source, for tests.</summary>
    public static IReadOnlyList<string> NewSet(Func<string>? next = null)
    {
        next ??= Generate;
        var codes = new List<string>(SetSize);
        var lookups = new HashSet<string>(StringComparer.Ordinal);
        while (codes.Count < SetSize)
        {
            var code = next();
            if (lookups.Add(code[..GroupLength]))
            {
                codes.Add(code);
            }
        }

        return codes;
    }

    /// <summary>Reads what a person typed: any case, with or without spaces and dashes. False when it cannot be a code.</summary>
    public static bool TryParse(string? typed, out string code, out string lookup)
    {
        var text = new string([.. (typed ?? string.Empty).ToUpperInvariant().Where(c => c is not ('-' or ' '))]);
        if (text.Length != GroupLength * GroupCount || !text.All(c => Alphabet.Contains(c, StringComparison.Ordinal)))
        {
            code = string.Empty;
            lookup = string.Empty;
            return false;
        }

        code = string.Join('-', Enumerable.Range(0, GroupCount).Select(i => text.Substring(i * GroupLength, GroupLength)));
        lookup = text[..GroupLength];
        return true;
    }
}
