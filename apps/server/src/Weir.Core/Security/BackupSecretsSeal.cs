using System.Security.Cryptography;
using System.Text;
using Weir.Core.Json;

namespace Weir.Core.Security;

/// <summary>
/// The secrets inside a backup, locked with a key made from the backup password: Argon2id turns the password into a key and
/// AES-GCM seals the text and says, by its tag, whether the password was the right one. Everything else in the backup stays
/// readable, so a restore can show what is inside before it asks for the password.
/// </summary>
public static class BackupSecretsSeal
{
    /// <summary>The backup's property that holds the sealed secrets.</summary>
    public const string SectionName = "sealed_secrets";

    public const int KeyLength = 32;
    public const int SaltLength = 16;
    public const int TimeCost = 3;
    public const int MemoryCostKib = 65_536;
    public const int Parallelism = 1;

    private const int FormatVersion = 1;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int MaxMemoryKib = 262_144;
    private const int MaxTimeCost = 10;
    private const int MaxParallelism = 4;
    private static readonly byte[] AssociatedData = "weir-backup-secrets-v1"u8.ToArray();

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltLength);

    /// <summary>The key a password gives with a salt, at the cost every new backup is sealed with.</summary>
    public static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt) =>
        DeriveKey(password, salt, TimeCost, MemoryCostKib, Parallelism);

    public static WireObject Seal(string secretsJson, ReadOnlySpan<byte> key, ReadOnlySpan<byte> salt)
    {
        ArgumentNullException.ThrowIfNull(secretsJson);
        var plain = Encoding.UTF8.GetBytes(secretsJson);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagLength];
        using (var aes = new AesGcm(key, TagLength))
        {
            aes.Encrypt(nonce, plain, cipher, tag, AssociatedData);
        }

        return new WireObject()
            .Set("format", FormatVersion)
            .Set("kdf", "argon2id")
            .Set("memory_kib", MemoryCostKib)
            .Set("iterations", TimeCost)
            .Set("parallelism", Parallelism)
            .Set("salt", Convert.ToBase64String(salt))
            .Set("nonce", Convert.ToBase64String(nonce))
            .Set("tag", Convert.ToBase64String(tag))
            .Set("ciphertext", Convert.ToBase64String(cipher));
    }

    /// <summary>
    /// The secrets' text, or <see langword="null"/> when the password is not the one they were sealed with (or the section was
    /// changed). Throws <see cref="WireValueException"/> when the section is not one this version of Weir can read.
    /// </summary>
    public static string? TryOpen(WireValue? section, string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var sealedSection = section as WireObject ?? throw Unreadable();
        if (Number(sealedSection, "format") != FormatVersion || Text(sealedSection, "kdf") != "argon2id")
        {
            throw Unreadable();
        }

        var memory = Number(sealedSection, "memory_kib");
        var time = Number(sealedSection, "iterations");
        var lanes = Number(sealedSection, "parallelism");
        if (memory is < 8 or > MaxMemoryKib || time is < 1 or > MaxTimeCost || lanes is < 1 or > MaxParallelism)
        {
            throw Unreadable();
        }

        var salt = Bytes(sealedSection, "salt");
        var nonce = Bytes(sealedSection, "nonce");
        var tag = Bytes(sealedSection, "tag");
        var cipher = Bytes(sealedSection, "ciphertext");
        if (salt.Length < 8 || nonce.Length != NonceLength || tag.Length != TagLength)
        {
            throw Unreadable();
        }

        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(DeriveKey(password, salt, (int)time, (int)memory, (int)lanes), TagLength);
            aes.Decrypt(nonce, cipher, tag, plain, AssociatedData);
        }
        catch (CryptographicException)
        {
            return null;
        }

        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt, int time, int memoryKib, int lanes) =>
        Argon2.Hash(Argon2Type.Argon2id, Argon2.Version13, Encoding.UTF8.GetBytes(password), salt, [], [], time, memoryKib, lanes, KeyLength);

    private static WireValueException Unreadable() =>
        new("This backup's locked secrets are not in a form this version of Weir can read.");

    private static long Number(WireObject section, string key) =>
        section.Get(key) is WireInteger number && number.Value >= int.MinValue && number.Value <= int.MaxValue ? (long)number.Value : throw Unreadable();

    private static string Text(WireObject section, string key) =>
        section.Get(key) is WireString text ? text.Value : throw Unreadable();

    private static byte[] Bytes(WireObject section, string key)
    {
        try
        {
            return Convert.FromBase64String(Text(section, key));
        }
        catch (FormatException)
        {
            throw Unreadable();
        }
    }
}
