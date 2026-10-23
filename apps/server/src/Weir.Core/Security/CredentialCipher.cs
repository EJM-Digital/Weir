using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Weir.Core.Json;

namespace Weir.Core.Security;

/// <summary>Fernet tokens (AES-128-CBC plus HMAC-SHA256), per the Fernet specification, so stored credentials stay readable.</summary>
public sealed class Fernet
{
    private readonly byte[] _signingKey;
    private readonly byte[] _encryptionKey;

    /// <param name="key">32 bytes: the signing key followed by the AES-128 key.</param>
    public Fernet(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
        {
            throw new ArgumentException("Fernet key must be 32 bytes.", nameof(key));
        }

        _signingKey = key[..16];
        _encryptionKey = key[16..];
    }

    public string Encrypt(byte[] plaintext, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(time);
        var iv = RandomNumberGenerator.GetBytes(16);
        return EncryptAt(plaintext, time.GetUtcNow().ToUnixTimeSeconds(), iv);
    }

    internal string EncryptAt(byte[] plaintext, long timestamp, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = _encryptionKey;
        var ciphertext = aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7);
        var basic = new byte[1 + 8 + 16 + ciphertext.Length];
        basic[0] = 0x80;
        BinaryPrimitives.WriteUInt64BigEndian(basic.AsSpan(1), (ulong)timestamp);
        iv.CopyTo(basic, 9);
        ciphertext.CopyTo(basic, 25);
        var mac = HMACSHA256.HashData(_signingKey, basic);
        return Convert.ToBase64String([.. basic, .. mac]).Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Decrypts without a TTL; <see langword="null"/> for a malformed, tampered or wrongly keyed token.</summary>
    public byte[]? Decrypt(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        byte[] data;
        try
        {
            var standard = token.Replace('-', '+').Replace('_', '/');
            if (standard.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')))
            {
                return null;
            }

            data = Convert.FromBase64String(standard);
        }
        catch (FormatException)
        {
            return null;
        }

        if (data.Length < 1 + 8 + 16 + 32 || data[0] != 0x80)
        {
            return null;
        }

        var expected = HMACSHA256.HashData(_signingKey, data.AsSpan(0, data.Length - 32));
        if (!CryptographicOperations.FixedTimeEquals(expected, data.AsSpan(data.Length - 32)))
        {
            return null;
        }

        try
        {
            using var aes = Aes.Create();
            aes.Key = _encryptionKey;
            return aes.DecryptCbc(data.AsSpan(25, data.Length - 25 - 32), data.AsSpan(9, 16), PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>
/// Stored media-manager and provider credentials: Fernet keyed by PBKDF2-SHA256 over the
/// credentials secret, in a <c>{"version":2,"key_id":…,"token":…}</c> envelope, with the legacy
/// session-secret fallback and rotation through previous credentials secrets.
/// </summary>
public sealed class CredentialCipher
{
    public const string CredentialsKeyId = "credentials:v1";
    public const string SessionLegacyKeyId = "session-legacy:v1";

    /// <summary>Frozen KDF domain bytes (legacy install compatibility). Do not change.</summary>
    private static readonly byte[] KdfPepper = Convert.FromHexString("6d656469616d6f702e666574636865722e6172725f6170695f6b65792e76317c");
    private const int KdfIterations = 390_000;
    private const int EnvelopeVersion = 2;

    private readonly string? _credentialsSecret;
    private readonly string? _sessionSecret;
    private readonly IReadOnlyList<string> _previousCredentialsSecrets;
    private readonly TimeProvider _time;

    public CredentialCipher(string? credentialsSecret, string? sessionSecret, IReadOnlyList<string> previousCredentialsSecrets, TimeProvider time)
    {
        _credentialsSecret = credentialsSecret;
        _sessionSecret = sessionSecret;
        _previousCredentialsSecrets = previousCredentialsSecrets;
        _time = time;
    }

    public const string MissingSecretMessage = "Cannot save library API keys until WEIR_CREDENTIALS_SECRET or WEIR_SESSION_SECRET is set.";

    /// <summary>Encrypts an API key into the envelope with the active secret. Throws <see cref="WireValueException"/> when no secret is configured.</summary>
    public string Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var (fernet, keyId) = Active();
        if (fernet is null)
        {
            throw new WireValueException(MissingSecretMessage);
        }

        var token = fernet.Encrypt(Encoding.UTF8.GetBytes(plaintext.Trim()), _time);
        return WireJsonWriter.Dumps(
            new WireObject().Set("version", EnvelopeVersion).Set("key_id", keyId).Set("token", token),
            WireJsonFormat.Compact);
    }

    /// <summary>Decrypts an envelope (or a bare legacy token); <see langword="null"/> when no configured secret opens it.</summary>
    public string? Decrypt(string? ciphertext)
    {
        var raw = (ciphertext ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        var envelope = DecodeEnvelope(raw);
        if (envelope is not null)
        {
            var token = envelope.Get("token") is { } t && t.IsTruthy ? WireConvert.Str(t) : string.Empty;
            var keyId = envelope.Get("key_id") is { } k && k.IsTruthy ? WireConvert.Str(k) : string.Empty;
            if (token.Length == 0)
            {
                return null;
            }

            var candidates = keyId == CredentialsKeyId
                ? CredentialCandidates()
                : [.. new[] { ForSecret(_sessionSecret) }.OfType<Fernet>()];
            foreach (var candidate in candidates)
            {
                var plain = TryDecryptText(candidate, token);
                if (plain is not null)
                {
                    return plain;
                }
            }

            return null;
        }

        var legacy = ForSecret(_sessionSecret);
        return legacy is null ? null : TryDecryptText(legacy, raw);
    }

    /// <summary>Re-encrypts under the current credentials secret; <see langword="null"/> when there is none or the value cannot be decrypted.</summary>
    public string? Rewrap(string ciphertext)
    {
        if (string.IsNullOrEmpty(_credentialsSecret))
        {
            return null;
        }

        var plain = Decrypt(ciphertext);
        return plain is null ? null : Encrypt(plain);
    }

    private (Fernet? Fernet, string KeyId) Active()
    {
        var credentials = ForSecret(_credentialsSecret);
        return credentials is not null ? (credentials, CredentialsKeyId) : (ForSecret(_sessionSecret), SessionLegacyKeyId);
    }

    private List<Fernet> CredentialCandidates()
    {
        var output = new List<Fernet>();
        foreach (var secret in new[] { _credentialsSecret }.Concat(_previousCredentialsSecrets))
        {
            if (ForSecret(secret) is { } fernet)
            {
                output.Add(fernet);
            }
        }

        return output;
    }

    internal static Fernet? ForSecret(string? secret)
    {
        var raw = (secret ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(raw), KdfPepper, KdfIterations, HashAlgorithmName.SHA256, 32);
        return new Fernet(key);
    }

    private static string? TryDecryptText(Fernet fernet, string token)
    {
        if (token.Any(c => c > 127))
        {
            return null;
        }

        var bytes = fernet.Decrypt(token);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static WireObject? DecodeEnvelope(string raw)
    {
        if (!raw.StartsWith('{'))
        {
            return null;
        }

        try
        {
            return WireJsonParser.Parse(raw) as WireObject;
        }
        catch (WireJsonDecodeException)
        {
            return null;
        }
    }
}
