using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Weir.Core.Security;

/// <summary>What <see cref="PasswordHasher.Verify"/> found.</summary>
public enum PasswordVerification
{
    Match,
    Mismatch,

    /// <summary>The stored value is not an Argon2 hash this build can read.</summary>
    InvalidHash,
}

/// <summary>
/// Argon2id password hashes in the PHC string format, with time cost 3, memory 65536 KiB, parallelism 1,
/// a 32-byte hash and a 16-byte salt. The format and parameters stay fixed so existing password hashes
/// keep verifying.
/// </summary>
public static class PasswordHasher
{
    public const int TimeCost = 3;
    public const int MemoryCostKib = 65_536;
    public const int Parallelism = 1;
    public const int HashLength = 32;
    public const int SaltLength = 16;

    private const string DummyPasswordPlain = "weir-login-padding-password";

    private static readonly Lazy<string> Dummy = new(() => Hash(DummyPasswordPlain), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The hash a failed lookup verifies against so a missing user costs the same time.</summary>
    public static string DummyPasswordHash => Dummy.Value;

    public static string Hash(string plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        return HashWithSalt(plain, salt, Argon2Type.Argon2id, Argon2.Version13, TimeCost, MemoryCostKib, Parallelism, HashLength);
    }

    internal static string HashWithSalt(string plain, byte[] salt, Argon2Type type, int version, int timeCost, int memoryKib, int parallelism, int hashLength)
    {
        var tag = Argon2.Hash(type, version, Encoding.UTF8.GetBytes(plain), salt, [], [], timeCost, memoryKib, parallelism, hashLength);
        var name = type switch
        {
            Argon2Type.Argon2id => "argon2id",
            Argon2Type.Argon2i => "argon2i",
            _ => "argon2d",
        };
        var versionPart = version == Argon2.Version10 ? string.Empty : $"$v={version}";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"${name}{versionPart}$m={memoryKib},t={timeCost},p={parallelism}${EncodeB64(salt)}${EncodeB64(tag)}");
    }

    /// <summary>Verifies a password; the Argon2 parameters come from the stored hash.</summary>
    public static PasswordVerification Verify(string plain, string encoded)
    {
        ArgumentNullException.ThrowIfNull(plain);
        if (string.IsNullOrEmpty(encoded))
        {
            return PasswordVerification.Mismatch;
        }

        if (!TryDecode(encoded, out var decoded))
        {
            return PasswordVerification.InvalidHash;
        }

        var computed = Argon2.Hash(
            decoded.Type, decoded.Version, Encoding.UTF8.GetBytes(plain), decoded.Salt, [], [],
            decoded.TimeCost, decoded.MemoryKib, decoded.Parallelism, decoded.Tag.Length);

        return CryptographicOperations.FixedTimeEquals(computed, decoded.Tag) ? PasswordVerification.Match : PasswordVerification.Mismatch;
    }

    internal sealed record DecodedHash(Argon2Type Type, int Version, int MemoryKib, int TimeCost, int Parallelism, byte[] Salt, byte[] Tag);

    /// <summary>Parses the PHC string <c>$type[$v=V]$m=M,t=T,p=P$salt$hash</c> as the Argon2 reference implementation does.</summary>
    internal static bool TryDecode(string encoded, out DecodedHash decoded)
    {
        decoded = null!;
        Argon2Type type;
        string rest;
        if (encoded.StartsWith("$argon2id$", StringComparison.Ordinal))
        {
            type = Argon2Type.Argon2id;
            rest = encoded["$argon2id".Length..];
        }
        else if (encoded.StartsWith("$argon2i$", StringComparison.Ordinal))
        {
            type = Argon2Type.Argon2i;
            rest = encoded["$argon2i".Length..];
        }
        else if (encoded.StartsWith("$argon2d$", StringComparison.Ordinal))
        {
            type = Argon2Type.Argon2d;
            rest = encoded["$argon2d".Length..];
        }
        else
        {
            return false;
        }

        var parts = rest.Split('$');
        // parts[0] is empty (leading '$').
        var index = 1;
        var version = Argon2.Version10;
        if (parts[index].StartsWith("v=", StringComparison.Ordinal))
        {
            if (!TryDecimal(parts[index][2..], out version))
            {
                return false;
            }

            index++;
        }

        if (parts.Length != index + 3 || parts[0].Length != 0)
        {
            return false;
        }

        var parameters = parts[index].Split(',');
        if (parameters.Length != 3 ||
            !parameters[0].StartsWith("m=", StringComparison.Ordinal) || !TryDecimal(parameters[0][2..], out var memory) ||
            !parameters[1].StartsWith("t=", StringComparison.Ordinal) || !TryDecimal(parameters[1][2..], out var time) ||
            !parameters[2].StartsWith("p=", StringComparison.Ordinal) || !TryDecimal(parameters[2][2..], out var lanes))
        {
            return false;
        }

        if (!TryDecodeB64(parts[index + 1], out var salt) || !TryDecodeB64(parts[index + 2], out var tag) ||
            version is not (Argon2.Version10 or Argon2.Version13) || salt.Length < 8 || tag.Length < 4 ||
            time < 1 || lanes < 1 || memory < 8 * lanes)
        {
            return false;
        }

        decoded = new DecodedHash(type, version, memory, time, lanes, salt, tag);
        return true;
    }

    private static bool TryDecimal(string text, out int value)
    {
        value = 0;
        return text.Length > 0 && text.All(char.IsAsciiDigit) &&
            (text.Length == 1 || text[0] != '0') &&
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Standard base64 without padding, as libargon2 writes it.</summary>
    internal static string EncodeB64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static bool TryDecodeB64(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length == 0 || text.Length % 4 == 1 || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/')))
        {
            return false;
        }

        bytes = Convert.FromBase64String(text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '='));
        return EncodeB64(bytes) == text;
    }
}

/// <summary>Password strength rules; the refusal messages are shown to the operator as written.</summary>
public static class PasswordPolicy
{
    public const int MinPasswordLength = 8;

    private static readonly HashSet<string> WeakPasswords = new(StringComparer.Ordinal)
    {
        "admin", "changeme", "letmein", "weir", "password", "password1", "qwerty", "welcome",
    };

    /// <summary>The reason the password is refused, or <see langword="null"/> when it is acceptable.</summary>
    public static string? Validate(string? plain, string? username)
    {
        var password = plain ?? string.Empty;
        var normalized = password.Trim().ToLowerInvariant();
        var user = (username ?? string.Empty).Trim().ToLowerInvariant();
        if (CodePoints(password) < MinPasswordLength)
        {
            return $"Password must be at least {MinPasswordLength} characters.";
        }

        var withoutTrailingDigits = normalized.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (WeakPasswords.Contains(normalized) || WeakPasswords.Contains(withoutTrailingDigits))
        {
            return "Password is too common. Choose a stronger password.";
        }

        if (user.Length > 0 && normalized.Contains(user, StringComparison.Ordinal))
        {
            return "Password must not contain the username.";
        }

        var distinct = new HashSet<int>();
        for (var i = 0; i < password.Length; i += char.IsSurrogatePair(password, i) ? 2 : 1)
        {
            distinct.Add(char.IsSurrogatePair(password, i) ? char.ConvertToUtf32(password[i], password[i + 1]) : password[i]);
        }

        return distinct.Count < 4 ? "Password must use a wider mix of characters." : null;
    }

    private static int CodePoints(string value)
    {
        var count = 0;
        for (var i = 0; i < value.Length; i += char.IsSurrogatePair(value, i) ? 2 : 1)
        {
            count++;
        }

        return count;
    }
}
