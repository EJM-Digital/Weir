using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Weir.Core.Security;

/// <summary>
/// The token a used recovery code gives for choosing a new password. It names the account and carries a tag of the password hash it
/// was issued against, so it stops working the moment the password changes, and it runs out after <see cref="MaxAgeSeconds"/>.
/// </summary>
public static class RecoveryResetTokens
{
    public const string SignerSalt = "weir-recovery-v1";
    public const long MaxAgeSeconds = 15 * 60;

    private const int TagLength = 16;

    public static string Issue(string secret, long userId, string passwordHash, TimeProvider time) =>
        new TimestampSigner(secret, SignerSalt, time).Sign(userId.ToString(CultureInfo.InvariantCulture) + ":" + PasswordTag(passwordHash));

    /// <summary>The account and password tag in a token that is signed with <paramref name="secret"/> and not too old.</summary>
    public static bool TryRead(string secret, string token, TimeProvider time, out long userId, out string passwordTag)
    {
        userId = 0;
        passwordTag = string.Empty;
        var payload = new TimestampSigner(secret, SignerSalt, time).Unsign(token, MaxAgeSeconds);
        if (payload is null)
        {
            return false;
        }

        var parts = Encoding.UTF8.GetString(payload).Split(':');
        if (parts.Length != 2 || parts[1].Length == 0 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out userId))
        {
            return false;
        }

        passwordTag = parts[1];
        return true;
    }

    /// <summary>A short, one-way mark of a password hash, so a token can tell the password has changed since it was issued.</summary>
    public static string PasswordTag(string passwordHash) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash)))[..TagLength];
}
