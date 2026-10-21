using Weir.Core.Security;

namespace Weir.Core.Tests.Security;

/// <summary>BLAKE2b produces between 1 and 64 bytes; anything else is refused.</summary>
public sealed class Blake2bOutputLengthTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void An_output_length_outside_one_to_sixty_four_is_refused(int length) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Blake2b.Hash("data"u8.ToArray(), new byte[length]));

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public void The_shortest_and_longest_outputs_are_produced(int length)
    {
        var output = new byte[length];

        Blake2b.Hash("data"u8, output);

        Assert.Contains(output, value => value != 0);
    }
}
