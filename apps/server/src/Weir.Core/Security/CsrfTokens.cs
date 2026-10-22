using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

// The token format signs with HMAC-SHA1 and derives its key with SHA1; it stays fixed so tokens already issued keep verifying.
#pragma warning disable CA5350

namespace Weir.Core.Security;

/// <summary>
/// Timestamped signatures: <c>value.timestamp.signature</c> with HMAC-SHA1, the key derived as
/// <c>SHA1(salt + "signer" + secret)</c>, and URL-safe base64 without padding for the timestamp and the
/// signature. The format stays fixed so CSRF tokens already issued keep verifying.
/// </summary>
public sealed class TimestampSigner
{
    private const byte Separator = (byte)'.';
    private readonly byte[] _key;
    private readonly TimeProvider _time;

    public TimestampSigner(string secret, string salt, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(salt);
        _time = time;
        _key = SHA1.HashData([.. Encoding.UTF8.GetBytes(salt), .. "signer"u8, .. Encoding.UTF8.GetBytes(secret)]);
    }

    public string Sign(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var timestamp = UrlSafeB64Encode(IntToBytes((ulong)Now()));
        var signed = Encoding.UTF8.GetBytes(value + "." + timestamp);
        return value + "." + timestamp + "." + UrlSafeB64Encode(HMACSHA1.HashData(_key, signed));
    }

    /// <summary>
    /// The payload when the signature matches and the token is no older
    /// than <paramref name="maxAgeSeconds"/> (and not from the future); otherwise <see langword="null"/>.
    /// </summary>
    public byte[]? Unsign(string token, long maxAgeSeconds)
    {
        ArgumentNullException.ThrowIfNull(token);
        var signed = Encoding.UTF8.GetBytes(token);
        var sigSep = Array.LastIndexOf(signed, Separator);
        if (sigSep < 0)
        {
            return null;
        }

        var value = signed[..sigSep];
        var signature = UrlSafeB64Decode(signed.AsSpan(sigSep + 1));
        if (signature is null || !CryptographicOperations.FixedTimeEquals(signature, HMACSHA1.HashData(_key, value)))
        {
            return null;
        }

        var tsSep = Array.LastIndexOf(value, Separator);
        if (tsSep < 0)
        {
            return null;
        }

        var tsBytes = UrlSafeB64Decode(value.AsSpan(tsSep + 1));
        if (tsBytes is null || tsBytes.Length > 8)
        {
            return null;
        }

        ulong timestamp = 0;
        foreach (var b in tsBytes)
        {
            timestamp = (timestamp << 8) | b;
        }

        var age = (decimal)Now() - timestamp;
        if (age > maxAgeSeconds || age < 0)
        {
            return null;
        }

        return value[..tsSep];
    }

    private long Now() => _time.GetUtcNow().ToUnixTimeSeconds();

    private static byte[] IntToBytes(ulong value)
    {
        var bytes = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        var first = 0;
        while (first < bytes.Length && bytes[first] == 0)
        {
            first++;
        }

        return bytes[first..];
    }

    public static string UrlSafeB64Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Lenient URL-safe base64 decode: non-ASCII and any other characters outside the alphabet are
    /// skipped and padding is added; <see langword="null"/> when what remains is not valid base64.
    /// </summary>
    internal static byte[]? UrlSafeB64Decode(ReadOnlySpan<byte> raw)
    {
        var alphabet = new StringBuilder(raw.Length + 3);
        foreach (var b in raw)
        {
            var c = (char)b switch
            {
                '-' => '+',
                '_' => '/',
                var other => other,
            };
            if (b <= 127 && (char.IsAsciiLetterOrDigit(c) || c is '+' or '/'))
            {
                alphabet.Append(c);
            }
        }

        var data = alphabet.ToString();
        if (data.Length % 4 == 1)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(data + new string('=', (4 - (data.Length % 4)) % 4));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>CSRF token issue and verification, bound to the session when there is one.</summary>
public static class CsrfTokens
{
    public const string SignerSalt = "weir-csrf-v1";
    public const long MaxAgeSeconds = 3600;
    public const string SubjectAnonymous = "csrf.anonymous";
    public const string SubjectSession = "csrf.session";

    public static string Issue(string secret, string? rawSessionToken, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Trim().Length == 0)
        {
            throw new ArgumentException("session secret required for CSRF", nameof(secret));
        }

        var bound = (rawSessionToken ?? string.Empty).Trim();
        var payload = bound.Length > 0 ? SessionSubject(bound) : SubjectAnonymous;
        return new TimestampSigner(secret, SignerSalt, time).Sign(payload);
    }

    public static bool Verify(string? secret, string? token, string? rawSessionToken, bool allowAnonymous, TimeProvider time, long maxAgeSeconds = MaxAgeSeconds)
    {
        if ((token ?? string.Empty).Trim().Length == 0 || (secret ?? string.Empty).Trim().Length == 0)
        {
            return false;
        }

        var raw = new TimestampSigner(secret!, SignerSalt, time).Unsign(token!, maxAgeSeconds);
        if (raw is null)
        {
            return false;
        }

        var payload = Encoding.UTF8.GetString(raw);
        if (allowAnonymous && payload == SubjectAnonymous)
        {
            return true;
        }

        if ((rawSessionToken ?? string.Empty).Trim().Length == 0)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(payload), Encoding.UTF8.GetBytes(SessionSubject(rawSessionToken!)));
    }

    private static string SessionSubject(string rawSessionToken) => SubjectSession + ":" + SessionTokens.Hash(rawSessionToken.Trim());
}

/// <summary>Opaque session tokens.</summary>
public static class SessionTokens
{
    /// <summary>32 random bytes as URL-safe base64 without padding.</summary>
    public static string Generate() => TimestampSigner.UrlSafeB64Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>The SHA-256 hex digest stored in <c>user_sessions.token_hash</c>.</summary>
    public static string Hash(string rawToken)
    {
        ArgumentNullException.ThrowIfNull(rawToken);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
    }
}
