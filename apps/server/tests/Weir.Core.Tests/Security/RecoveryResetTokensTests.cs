using Microsoft.Extensions.Time.Testing;
using Weir.Core.Security;

namespace Weir.Core.Tests.Security;

/// <summary>The token a used recovery code gives for choosing a new password: signed, short-lived, and dead once the password changes.</summary>
public sealed class RecoveryResetTokensTests
{
    private const string Secret = "recovery-test-secret-recovery-test-secret";

    [Fact]
    public void A_token_names_its_account_and_the_password_it_was_issued_against()
    {
        var time = new FakeTimeProvider();
        var token = RecoveryResetTokens.Issue(Secret, 7, "hash-one", time);

        Assert.True(RecoveryResetTokens.TryRead(Secret, token, time, out var userId, out var passwordTag));

        Assert.Equal(7, userId);
        Assert.Equal(RecoveryResetTokens.PasswordTag("hash-one"), passwordTag);
        Assert.NotEqual(RecoveryResetTokens.PasswordTag("hash-two"), passwordTag);
    }

    [Fact]
    public void A_token_signed_with_another_secret_or_altered_is_refused()
    {
        var time = new FakeTimeProvider();
        var token = RecoveryResetTokens.Issue(Secret, 7, "hash-one", time);

        Assert.False(RecoveryResetTokens.TryRead("another-secret-another-secret-another", token, time, out _, out _));
        Assert.False(RecoveryResetTokens.TryRead(Secret, "8" + token[1..], time, out _, out _));
        Assert.False(RecoveryResetTokens.TryRead(Secret, string.Empty, time, out _, out _));
    }

    [Fact]
    public void A_token_runs_out_after_fifteen_minutes()
    {
        var time = new FakeTimeProvider();
        var token = RecoveryResetTokens.Issue(Secret, 7, "hash-one", time);

        time.Advance(TimeSpan.FromMinutes(14));
        Assert.True(RecoveryResetTokens.TryRead(Secret, token, time, out _, out _));

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(RecoveryResetTokens.TryRead(Secret, token, time, out _, out _));
    }

    [Fact]
    public void A_signed_value_that_is_not_an_account_and_a_tag_is_refused()
    {
        var time = new FakeTimeProvider();
        var signer = new TimestampSigner(Secret, RecoveryResetTokens.SignerSalt, time);

        foreach (var value in new[] { "no-colon", "x:abc", "7:" })
        {
            Assert.False(RecoveryResetTokens.TryRead(Secret, signer.Sign(value), time, out _, out _), value);
        }
    }
}
