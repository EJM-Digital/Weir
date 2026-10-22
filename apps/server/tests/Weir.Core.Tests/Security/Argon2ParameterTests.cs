using Weir.Core.Security;

namespace Weir.Core.Tests.Security;

/// <summary>Argon2 refuses parameters it cannot honour instead of producing a weak or meaningless hash.</summary>
public sealed class Argon2ParameterTests
{
    private static byte[] Hash(int version = Argon2.Version13, int iterations = 1, int memoryKib = 8, int parallelism = 1, int hashLength = 32) =>
        Argon2.Hash(Argon2Type.Argon2id, version, "password"u8, "somesalt"u8, default, default, iterations, memoryKib, parallelism, hashLength);

    [Theory]
    [InlineData(0, 8, 1, 32)]
    [InlineData(1, 8, 0, 32)]
    [InlineData(1, 8, 1, 3)]
    [InlineData(1, 15, 2, 32)]
    public void Parameters_out_of_range_are_refused(int iterations, int memoryKib, int parallelism, int hashLength) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Hash(iterations: iterations, memoryKib: memoryKib, parallelism: parallelism, hashLength: hashLength));

    [Fact]
    public void An_unknown_version_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Hash(version: 0x11));

    [Fact]
    public void Both_known_versions_hash_to_the_length_asked_for()
    {
        Assert.Equal(32, Hash(Argon2.Version13).Length);
        Assert.Equal(16, Hash(Argon2.Version10, hashLength: 16).Length);
    }
}
