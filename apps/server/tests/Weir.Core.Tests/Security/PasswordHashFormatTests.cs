using Weir.Core.Security;

namespace Weir.Core.Tests.Security;

/// <summary>The stored form of a password hash for each Argon2 variant, and every way a stored hash can be unreadable.</summary>
public sealed class PasswordHashFormatTests
{
    private static readonly byte[] Salt = "somesalt-16bytes"u8.ToArray();

    private const string Valid = "$argon2id$v=19$m=64,t=1,p=1$c29tZXNhbHQtMTZieXRlcw$AAECAwQFBgcICQoLDA0ODw";

    [Theory]
    [InlineData(Argon2Type.Argon2id, Argon2.Version13, "$argon2id$v=19$m=64,t=1,p=1$")]
    [InlineData(Argon2Type.Argon2i, Argon2.Version13, "$argon2i$v=19$m=64,t=1,p=1$")]
    [InlineData(Argon2Type.Argon2d, Argon2.Version13, "$argon2d$v=19$m=64,t=1,p=1$")]
    [InlineData(Argon2Type.Argon2id, Argon2.Version10, "$argon2id$m=64,t=1,p=1$")]
    public void Each_variant_is_written_with_its_name_and_a_version_only_when_it_is_not_the_oldest(Argon2Type type, int version, string start)
    {
        var encoded = PasswordHasher.HashWithSalt("a password", Salt, type, version, timeCost: 1, memoryKib: 64, parallelism: 1, hashLength: 16);

        Assert.StartsWith(start, encoded, StringComparison.Ordinal);
        Assert.Equal(PasswordVerification.Match, PasswordHasher.Verify("a password", encoded));
        Assert.Equal(PasswordVerification.Mismatch, PasswordHasher.Verify("another password", encoded));
    }

    [Fact]
    public void The_hash_used_to_spend_the_same_time_on_an_unknown_user_is_a_real_hash_of_a_password_nobody_has()
    {
        Assert.Equal(PasswordVerification.Match, PasswordHasher.Verify("weir-login-padding-password", PasswordHasher.DummyPasswordHash));
        Assert.Equal(PasswordVerification.Mismatch, PasswordHasher.Verify("guess", PasswordHasher.DummyPasswordHash));
        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", PasswordHasher.DummyPasswordHash, StringComparison.Ordinal);
    }

    [Fact]
    public void A_password_must_be_given()
    {
        Assert.Throws<ArgumentNullException>(() => PasswordHasher.Hash(null!));
        Assert.Throws<ArgumentNullException>(() => PasswordHasher.Verify(null!, Valid));
    }

    [Fact]
    public void A_well_formed_hash_is_read_back_into_its_parts()
    {
        Assert.True(PasswordHasher.TryDecode(Valid, out var decoded));
        Assert.Equal(Argon2Type.Argon2id, decoded.Type);
        Assert.Equal(Argon2.Version13, decoded.Version);
        Assert.Equal((64, 1, 1), (decoded.MemoryKib, decoded.TimeCost, decoded.Parallelism));
        Assert.Equal(Salt, decoded.Salt);
        Assert.Equal(16, decoded.Tag.Length);
    }

    [Fact]
    public void A_hash_without_a_version_is_the_oldest_version()
    {
        Assert.True(PasswordHasher.TryDecode("$argon2i$m=64,t=1,p=1$c29tZXNhbHQtMTZieXRlcw$AAECAwQFBgcICQoLDA0ODw", out var decoded));
        Assert.Equal(Argon2.Version10, decoded.Version);
        Assert.Equal(Argon2Type.Argon2i, decoded.Type);
        Assert.True(PasswordHasher.TryDecode(Valid.Replace("argon2id", "argon2d", StringComparison.Ordinal), out var data));
        Assert.Equal(Argon2Type.Argon2d, data.Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("$argon2$v=19$m=64,t=1,p=1$c29tZXNhbHQtMTZieXRlcw$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("argon2id$v=19$m=64,t=1,p=1$c29tZXNhbHQtMTZieXRlcw$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("$argon2id$v=x$m=64,t=1,p=1$c29tZXNhbHQtMTZieXRlcw$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("$argon2id$v=20$m=64,t=1,p=1$c29tZXNhbHQtMTZieXRlcw$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("$argon2id$v=019$m=64,t=1,p=1$c29tZXNhbHQtMTZieXRlcw$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("$argon2id$v=19$m=64,t=1,p=1$c29tZXNhbHQtMTZieXRlcw")]
    [InlineData("$argon2id$v=19$m=64,t=1,p=1$c29tZXNhbHQtMTZieXRlcw$AAECAwQFBgcICQoLDA0ODw$extra")]
    [InlineData("$argon2id$v=19$t=1,m=64,p=1$c29tZXNhbHQtMTZieXRlcw$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("$argon2id$v=19$m=64,p=1,t=1$c29tZXNhbHQtMTZieXRlcw$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("$argon2id$v=19$m=64,t=1,q=1$c29tZXNhbHQtMTZieXRlcw$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("$argon2id$v=19$m=64,t=1,p=1,x=2$c29tZXNhbHQtMTZieXRlcw$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("$argon2id$v=19$m=064,t=1,p=1$c29tZXNhbHQtMTZieXRlcw$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("$argon2id$v=19$m=-64,t=1,p=1$c29tZXNhbHQtMTZieXRlcw$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("$argon2id$v=19$m=99999999999,t=1,p=1$c29tZXNhbHQtMTZieXRlcw$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("$argon2id$v=19$m=64,t=0,p=1$c29tZXNhbHQtMTZieXRlcw$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("$argon2id$v=19$m=64,t=1,p=0$c29tZXNhbHQtMTZieXRlcw$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("$argon2id$v=19$m=7,t=1,p=1$c29tZXNhbHQtMTZieXRlcw$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("$argon2id$v=19$m=15,t=1,p=2$c29tZXNhbHQtMTZieXRlcw$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("$argon2id$v=19$m=64,t=1,p=1$c2FsdA$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("$argon2id$v=19$m=64,t=1,p=1$c29tZXNhbHQtMTZieXRlcw$AAEC")]
    [InlineData("$argon2id$v=19$m=64,t=1,p=1$$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("$argon2id$v=19$m=64,t=1,p=1$c29tZXNhbHQtMTZieXRlcw!$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("$argon2id$v=19$m=64,t=1,p=1$c29tZXNhbHQtMTZieXRlcw=$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("$argon2id$v=19$m=64,t=1,p=1$c29tZXNhbHQtMTZieXRlcx$AAECAwQFBgcICQoLDA0ODw")]
    [InlineData("$argon2id$v=19$m=64,t=1,p=1$c29tZXNhbHQtMTZieXRlc$AAECAwQFBgcICQoLDA0ODw")]
    public void A_stored_hash_that_is_not_in_the_expected_form_is_not_read(string stored)
    {
        Assert.False(PasswordHasher.TryDecode(stored, out _));
        Assert.Equal(stored.Length == 0 ? PasswordVerification.Mismatch : PasswordVerification.InvalidHash, PasswordHasher.Verify("a password", stored));
    }

    [Theory]
    [InlineData("12345678", "someone", null)]
    [InlineData("1234567", "someone", "Password must be at least 8 characters.")]
    [InlineData("", "someone", "Password must be at least 8 characters.")]
    [InlineData("Password1", "someone", "Password is too common. Choose a stronger password.")]
    [InlineData("  QWERTY  ", "someone", "Password is too common. Choose a stronger password.")]
    [InlineData("qwerty123456", "someone", "Password is too common. Choose a stronger password.")]
    [InlineData("xxSomeoneyy99", "someone", "Password must not contain the username.")]
    [InlineData("aaaaaaaaaaaa", "someone", "Password must use a wider mix of characters.")]
    [InlineData("abababababab", "someone", "Password must use a wider mix of characters.")]
    [InlineData("correct horse battery", "someone", null)]
    public void A_password_is_refused_in_plain_words_or_accepted(string password, string username, string? refusal) =>
        Assert.Equal(refusal, PasswordPolicy.Validate(password, username));

    [Fact]
    public void A_missing_password_or_username_is_treated_as_empty_and_characters_beyond_the_basic_plane_count_once()
    {
        Assert.Equal("Password must be at least 8 characters.", PasswordPolicy.Validate(null, null));
        Assert.Null(PasswordPolicy.Validate("pásswörd!", null));
        Assert.Equal("Password must be at least 8 characters.", PasswordPolicy.Validate("\U0001F600\U0001F601\U0001F602\U0001F603\U0001F604\U0001F605\U0001F606", "someone"));
        Assert.Null(PasswordPolicy.Validate("\U0001F600\U0001F601\U0001F602\U0001F603\U0001F604\U0001F605\U0001F606\U0001F607", "someone"));
        Assert.Equal("Password must use a wider mix of characters.", PasswordPolicy.Validate("\U0001F600\U0001F600\U0001F600\U0001F600\U0001F600\U0001F600\U0001F600\U0001F600", null));
    }
}
