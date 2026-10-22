using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Weir.Core.Security;

#pragma warning disable CA5350 // The token format signs with HMAC-SHA1; these tests build tokens in that fixed format.

namespace Weir.Core.Tests.Security;

/// <summary>
/// Tokens that are damaged, forged or out of date are refused, and the lenient reading of the signed parts. The tokens are signed here
/// with the documented format (<c>value.timestamp.signature</c>, HMAC-SHA1 over a key derived from the salt and the secret).
/// </summary>
public sealed class CsrfTokenEdgeTests
{
    private const string Secret = "a-session-secret";

    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    private static string Sign(string signedPart, string secret = Secret)
    {
        var key = SHA1.HashData(Encoding.UTF8.GetBytes(CsrfTokens.SignerSalt + "signer" + secret));
        return signedPart + "." + TimestampSigner.UrlSafeB64Encode(HMACSHA1.HashData(key, Encoding.UTF8.GetBytes(signedPart)));
    }

    private static string Timestamp(long seconds)
    {
        var bytes = BitConverter.GetBytes((ulong)seconds).Reverse().SkipWhile(b => b == 0).ToArray();
        return TimestampSigner.UrlSafeB64Encode(bytes);
    }

    private static TimestampSigner Signer() => new(Secret, CsrfTokens.SignerSalt, Clock);

    [Theory]
    [InlineData("no-dots-at-all")]
    [InlineData("payload.A")]
    [InlineData("payload.not-the-signature")]
    [InlineData("payload.QUJDRA")]
    [InlineData("")]
    public void A_token_whose_signature_is_missing_or_wrong_is_refused(string token)
    {
        Assert.Null(Signer().Unsign(token, 3600));
    }

    [Fact]
    public void A_signed_value_with_no_timestamp_is_refused()
    {
        Assert.Null(Signer().Unsign(Sign("payload-only"), 3600));
    }

    [Fact]
    public void A_signed_value_with_an_unreadable_timestamp_is_refused()
    {
        Assert.Null(Signer().Unsign(Sign("payload.A"), 3600));
    }

    [Fact]
    public void A_signed_value_with_a_timestamp_longer_than_eight_bytes_is_refused()
    {
        var nineBytes = TimestampSigner.UrlSafeB64Encode(new byte[9].Select((_, i) => (byte)(i + 1)).ToArray());

        Assert.Null(Signer().Unsign(Sign("payload." + nineBytes), 3600));
    }

    [Fact]
    public void A_token_from_the_future_or_past_its_age_is_refused_and_one_in_range_is_read()
    {
        var now = Clock.GetUtcNow().ToUnixTimeSeconds();

        Assert.Equal("payload"u8.ToArray(), Signer().Unsign(Sign("payload." + Timestamp(now - 10)), 3600));
        Assert.Null(Signer().Unsign(Sign("payload." + Timestamp(now - 3601)), 3600));
        Assert.Null(Signer().Unsign(Sign("payload." + Timestamp(now + 10)), 3600));
    }

    [Fact]
    public void A_token_signed_at_the_very_start_of_the_clock_is_read_back()
    {
        var epoch = new TimestampSigner(Secret, CsrfTokens.SignerSalt, new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        Assert.Equal("payload"u8.ToArray(), epoch.Unsign(epoch.Sign("payload"), 3600));
    }

    [Theory]
    [InlineData("QUJD", "ABC")]
    [InlineData("QUJD=", "ABC")]
    [InlineData("QUéJD", "ABC")]
    [InlineData("QU!JD", "ABC")]
    public void The_base64_of_a_signed_part_is_read_leniently(string text, string expected)
    {
        var decoded = TimestampSigner.UrlSafeB64Decode(Encoding.UTF8.GetBytes(text));

        Assert.NotNull(decoded);
        Assert.Equal(Encoding.Latin1.GetBytes(expected), decoded);
    }

    [Fact]
    public void The_url_safe_characters_of_base64_read_as_the_plain_ones()
    {
        Assert.Equal(TimestampSigner.UrlSafeB64Decode("QU+/"u8), TimestampSigner.UrlSafeB64Decode("QU-_"u8));
    }

    [Fact]
    public void Base64_with_a_dangling_character_is_not_readable()
    {
        Assert.Null(TimestampSigner.UrlSafeB64Decode("Q"u8));
        Assert.Null(TimestampSigner.UrlSafeB64Decode("QUJDR"u8));
    }

    [Fact]
    public void A_blank_session_secret_cannot_issue_a_token()
    {
        Assert.Throws<ArgumentException>(() => CsrfTokens.Issue("   ", null, Clock));
    }

    [Theory]
    [InlineData(null, "token")]
    [InlineData("  ", "token")]
    [InlineData(Secret, null)]
    [InlineData(Secret, "   ")]
    public void A_missing_token_or_secret_never_verifies(string? secret, string? token)
    {
        Assert.False(CsrfTokens.Verify(secret, token, "session", allowAnonymous: true, Clock));
    }

    [Fact]
    public void An_anonymous_token_verifies_only_where_anonymous_use_is_allowed()
    {
        var token = CsrfTokens.Issue(Secret, null, Clock);

        Assert.True(CsrfTokens.Verify(Secret, token, null, allowAnonymous: true, Clock));
        Assert.False(CsrfTokens.Verify(Secret, token, null, allowAnonymous: false, Clock));
        Assert.False(CsrfTokens.Verify(Secret, token, "   ", allowAnonymous: false, Clock));
    }

    [Fact]
    public void A_session_token_verifies_only_for_the_session_it_was_issued_to()
    {
        var token = CsrfTokens.Issue(Secret, " session-one ", Clock);

        Assert.True(CsrfTokens.Verify(Secret, token, "session-one", allowAnonymous: false, Clock));
        Assert.False(CsrfTokens.Verify(Secret, token, "session-two", allowAnonymous: false, Clock));
        Assert.False(CsrfTokens.Verify(Secret, token, null, allowAnonymous: true, Clock));
    }

    [Fact]
    public void A_token_signed_with_another_secret_does_not_verify()
    {
        var token = CsrfTokens.Issue("another-secret", null, Clock);

        Assert.False(CsrfTokens.Verify(Secret, token, null, allowAnonymous: true, Clock));
    }
}
