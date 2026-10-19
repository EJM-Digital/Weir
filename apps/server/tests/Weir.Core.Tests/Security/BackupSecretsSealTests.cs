using Weir.Core.Json;
using Weir.Core.Security;

namespace Weir.Core.Tests.Security;

/// <summary>The secrets in a backup open only with the password they were sealed with, and never appear in the clear.</summary>
public sealed class BackupSecretsSealTests
{
    private const string Secrets = """{"api_key":"sonarr-key-7f3a91","password":"tr0ub4dor&3-client"}""";
    private const string Password = "correct horse battery";

    private static readonly byte[] Salt = Enumerable.Range(1, BackupSecretsSeal.SaltLength).Select(value => (byte)value).ToArray();

    private static WireObject Sealed() => BackupSecretsSeal.Seal(Secrets, BackupSecretsSeal.DeriveKey(Password, Salt), Salt);

    private static WireObject Tampered(string key, WireValue value)
    {
        var section = Sealed();
        section.Set(key, value);
        return section;
    }

    [Fact]
    public void The_right_password_gives_the_secrets_back()
    {
        Assert.Equal(Secrets, BackupSecretsSeal.TryOpen(Sealed(), Password));
    }

    [Fact]
    public void A_wrong_password_gives_nothing_back()
    {
        Assert.Null(BackupSecretsSeal.TryOpen(Sealed(), "correct horse battery!"));
        Assert.Null(BackupSecretsSeal.TryOpen(Sealed(), string.Empty));
    }

    [Fact]
    public void The_sealed_section_holds_no_secret_in_the_clear_and_names_how_it_was_made()
    {
        var text = WireJsonWriter.Dumps(Sealed(), WireJsonFormat.Compact);

        foreach (var secret in new[] { "sonarr-key-7f3a91", "tr0ub4dor&3-client", Password })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }

        Assert.Contains("\"kdf\":\"argon2id\"", text, StringComparison.Ordinal);
        Assert.Equal(BackupSecretsSeal.MemoryCostKib, ((WireInteger)Sealed().Get("memory_kib")!).Value);
    }

    [Fact]
    public void Two_seals_of_the_same_secrets_differ()
    {
        Assert.NotEqual(WireJsonWriter.Dumps(Sealed(), WireJsonFormat.Compact), WireJsonWriter.Dumps(Sealed(), WireJsonFormat.Compact));
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    [InlineData("nonce")]
    public void A_section_that_was_changed_is_not_opened(string part)
    {
        var section = Sealed();
        var bytes = Convert.FromBase64String(((WireString)section.Get(part)!).Value);
        bytes[0] ^= 1;

        Assert.Null(BackupSecretsSeal.TryOpen(Tampered(part, new WireString(Convert.ToBase64String(bytes))), Password));
    }

    [Fact]
    public void A_different_salt_is_not_opened()
    {
        Assert.Null(BackupSecretsSeal.TryOpen(Tampered("salt", new WireString(Convert.ToBase64String(new byte[BackupSecretsSeal.SaltLength]))), Password));
    }

    [Fact]
    public void A_section_this_version_cannot_read_is_refused_in_plain_words()
    {
        var refused = new[]
        {
            Assert.Throws<WireValueException>(() => BackupSecretsSeal.TryOpen(new WireString("x"), Password)),
            Assert.Throws<WireValueException>(() => BackupSecretsSeal.TryOpen(null, Password)),
            Assert.Throws<WireValueException>(() => BackupSecretsSeal.TryOpen(Tampered("format", WireValue.Of(2L)), Password)),
            Assert.Throws<WireValueException>(() => BackupSecretsSeal.TryOpen(Tampered("kdf", new WireString("pbkdf2")), Password)),
            Assert.Throws<WireValueException>(() => BackupSecretsSeal.TryOpen(Tampered("memory_kib", WireValue.Of(4_000_000L)), Password)),
            Assert.Throws<WireValueException>(() => BackupSecretsSeal.TryOpen(Tampered("iterations", WireValue.Of(0L)), Password)),
            Assert.Throws<WireValueException>(() => BackupSecretsSeal.TryOpen(Tampered("parallelism", WireValue.Of(64L)), Password)),
            Assert.Throws<WireValueException>(() => BackupSecretsSeal.TryOpen(Tampered("nonce", new WireString(Convert.ToBase64String(new byte[3]))), Password)),
            Assert.Throws<WireValueException>(() => BackupSecretsSeal.TryOpen(Tampered("tag", new WireString("not base64!")), Password)),
            Assert.Throws<WireValueException>(() => BackupSecretsSeal.TryOpen(Tampered("ciphertext", WireValue.Of(7L)), Password)),
        };

        Assert.All(refused, error => Assert.Equal("This backup's locked secrets are not in a form this version of Weir can read.", error.Message));
    }
}
