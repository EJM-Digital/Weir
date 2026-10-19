using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Weir.Core.Json;
using Weir.Core.Security;

namespace Weir.Core.Tests.Security;

/// <summary>Stored credentials that cannot be read back, and the tokens and envelopes that must be refused rather than trusted.</summary>
public sealed class CredentialCipherEdgeTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    private static readonly FakeTimeProvider Clock = new(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

    private static string Token(Fernet fernet, string text) => fernet.Encrypt(Encoding.UTF8.GetBytes(text), Clock);

    private static string Envelope(string keyId, string token) =>
        WireJsonWriter.Dumps(new WireObject().Set("version", 2L).Set("key_id", keyId).Set("token", token), WireJsonFormat.Compact);

    [Fact]
    public void A_fernet_key_is_32_bytes()
    {
        Assert.Throws<ArgumentNullException>(() => new Fernet(null!));
        Assert.Throws<ArgumentException>(() => new Fernet(new byte[31]));
        Assert.Throws<ArgumentException>(() => new Fernet(new byte[33]));
    }

    [Fact]
    public void A_token_opens_with_the_key_that_made_it_and_arguments_must_be_given()
    {
        var fernet = new Fernet(Key);

        var token = Token(fernet, "arr-api-key");

        Assert.Equal("arr-api-key", Encoding.UTF8.GetString(fernet.Decrypt(token)!));
        Assert.Throws<ArgumentNullException>(() => fernet.Decrypt(null!));
        Assert.Throws<ArgumentNullException>(() => fernet.Encrypt(null!, Clock));
        Assert.Throws<ArgumentNullException>(() => fernet.Encrypt([], null!));
    }

    [Fact]
    public void A_token_made_with_another_key_or_changed_in_any_way_is_refused()
    {
        var fernet = new Fernet(Key);
        var token = Token(fernet, "arr-api-key");
        var bytes = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/'));
        string Changed(int index)
        {
            var copy = (byte[])bytes.Clone();
            copy[index] ^= 0x01;
            return Convert.ToBase64String(copy).Replace('+', '-').Replace('/', '_');
        }

        Assert.Null(new Fernet(Key.Reverse().ToArray()).Decrypt(token));
        Assert.Null(fernet.Decrypt(Changed(0)));
        Assert.Null(fernet.Decrypt(Changed(30)));
        Assert.Null(fernet.Decrypt(Changed(bytes.Length - 1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a token!")]
    [InlineData("gAAAA")]
    [InlineData("gAAAAA==X")]
    [InlineData("AAAA")]
    public void Text_that_is_not_a_token_is_refused(string text) => Assert.Null(new Fernet(Key).Decrypt(text));

    [Fact]
    public void A_correctly_signed_token_whose_contents_are_not_valid_is_refused()
    {
        var signingKey = Key[..16];
        var basic = new byte[1 + 8 + 16 + 16];
        basic[0] = 0x80;
        BinaryPrimitives.WriteUInt64BigEndian(basic.AsSpan(1), 1_700_000_000);
        var signed = Convert.ToBase64String([.. basic, .. HMACSHA256.HashData(signingKey, basic)]).Replace('+', '-').Replace('/', '_');

        Assert.Null(new Fernet(Key).Decrypt(signed));
    }

    [Fact]
    public void A_token_for_a_credential_that_is_not_text_is_refused_rather_than_guessed_at()
    {
        var cipher = new CredentialCipher("credentials-secret", null, [], Clock);
        var binary = CredentialCipher.ForSecret("credentials-secret")!.Encrypt([0xFF, 0xFE, 0xFD], Clock);

        Assert.Null(cipher.Decrypt(Envelope(CredentialCipher.CredentialsKeyId, binary)));
    }

    [Fact]
    public void A_token_with_characters_outside_ascii_is_refused()
    {
        var cipher = new CredentialCipher("credentials-secret", null, [], Clock);

        Assert.Null(cipher.Decrypt(Envelope(CredentialCipher.CredentialsKeyId, "gAAAAAé")));
    }

    [Fact]
    public void Blank_input_and_envelopes_that_hold_no_token_have_nothing_to_decrypt()
    {
        var cipher = new CredentialCipher("credentials-secret", "session-secret", [], Clock);

        Assert.Null(cipher.Decrypt(null));
        Assert.Null(cipher.Decrypt("   "));
        Assert.Null(cipher.Decrypt("{\"version\": 2, \"key_id\": \"credentials:v1\"}"));
        Assert.Null(cipher.Decrypt("{\"version\": 2, \"token\": \"\"}"));
    }

    [Fact]
    public void Text_that_starts_like_an_envelope_but_is_not_valid_json_is_tried_as_a_bare_token_and_refused()
    {
        var cipher = new CredentialCipher("credentials-secret", "session-secret", [], Clock);

        Assert.Null(cipher.Decrypt("{broken"));
        Assert.Null(new CredentialCipher("credentials-secret", null, [], Clock).Decrypt("gAAAAA"));
    }

    [Fact]
    public void An_envelope_names_the_key_that_opens_it_and_a_session_envelope_needs_the_session_secret()
    {
        var withSession = new CredentialCipher(null, "session-secret", [], Clock);
        var stored = withSession.Encrypt(" arr-key ");

        Assert.Equal("arr-key", withSession.Decrypt(stored));
        Assert.Null(new CredentialCipher(null, null, [], Clock).Decrypt(stored));
        Assert.Null(new CredentialCipher("credentials-secret", null, [], Clock).Decrypt(stored));
    }

    [Fact]
    public void A_value_cannot_be_rewrapped_without_a_credentials_secret_or_when_it_cannot_be_read()
    {
        var current = new CredentialCipher("credentials-secret", "session-secret", [], Clock);
        var stored = current.Encrypt("arr-key");

        Assert.Null(new CredentialCipher(null, "session-secret", [], Clock).Rewrap(stored));
        Assert.Null(new CredentialCipher(string.Empty, "session-secret", [], Clock).Rewrap(stored));
        Assert.Null(current.Rewrap("not a stored value"));
        Assert.Equal("arr-key", current.Decrypt(current.Rewrap(stored)));
    }

    [Fact]
    public void Nothing_is_saved_without_a_secret_and_a_value_must_be_given()
    {
        var none = new CredentialCipher("  ", "", [], Clock);

        Assert.Equal(CredentialCipher.MissingSecretMessage, Assert.Throws<WireValueException>(() => none.Encrypt("x")).Message);
        Assert.Throws<ArgumentNullException>(() => none.Encrypt(null!));
    }

    [Fact]
    public void A_secret_that_is_blank_has_no_key()
    {
        Assert.Null(CredentialCipher.ForSecret(null));
        Assert.Null(CredentialCipher.ForSecret("   "));
        Assert.NotNull(CredentialCipher.ForSecret(" secret "));
    }
}
