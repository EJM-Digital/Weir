using Weir.Core.Security;

namespace Weir.Core.Tests.Security;

/// <summary>
/// A recovery code is 100 random bits, which no amount of work per guess makes easier to guess, so it is hashed at a small fraction of
/// a password's cost. Ten of them are made with every account, and a browser test makes an account each: at a password's cost that
/// was seven seconds of work per account and doubled the length of the E2E jobs.
/// </summary>
public sealed class RandomSecretHashTests
{
    [Fact]
    public void A_random_secret_is_hashed_with_argon2id_at_the_small_cost_and_verifies_from_the_parameters_in_the_hash()
    {
        var hash = PasswordHasher.HashRandomSecret("K7M2P-9XQ4T-HWB3D-RN6YC");

        Assert.StartsWith("$argon2id$v=19$m=8192,t=1,p=1$", hash, StringComparison.Ordinal);
        Assert.Equal(PasswordVerification.Match, PasswordHasher.Verify("K7M2P-9XQ4T-HWB3D-RN6YC", hash));
        Assert.Equal(PasswordVerification.Mismatch, PasswordHasher.Verify("K7M2P-9XQ4T-HWB3D-RN6YD", hash));
    }

    [Fact]
    public void Each_hash_has_a_salt_of_its_own()
    {
        Assert.NotEqual(PasswordHasher.HashRandomSecret("same"), PasswordHasher.HashRandomSecret("same"));
    }

    [Fact]
    public void The_hash_a_missing_code_is_checked_against_costs_what_a_real_one_does()
    {
        Assert.StartsWith("$argon2id$v=19$m=8192,t=1,p=1$", PasswordHasher.DummyRandomSecretHash, StringComparison.Ordinal);
        Assert.Equal(PasswordVerification.Mismatch, PasswordHasher.Verify("anything", PasswordHasher.DummyRandomSecretHash));
    }

    [Fact]
    public void A_password_is_still_hashed_at_the_full_cost()
    {
        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", PasswordHasher.Hash("a password"), StringComparison.Ordinal);
    }
}
