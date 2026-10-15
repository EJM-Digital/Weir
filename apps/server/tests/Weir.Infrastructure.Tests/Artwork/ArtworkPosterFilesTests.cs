using Weir.Core.Artwork;

namespace Weir.Infrastructure.Tests.Artwork;

public sealed class ArtworkPosterFilesTests
{
    private static readonly string PosterId = ArtworkKeys.PosterId("poster.jpg");

    [Fact]
    public async Task A_poster_being_sent_can_be_removed_without_cutting_the_send_short()
    {
        using var fixture = new ArtworkFixture();
        await fixture.PosterFiles.WriteAsync(PosterId, [1, 2, 3], CancellationToken.None);
        await using var beingSent = fixture.PosterFiles.Open(PosterId);

        // Windows refuses to remove a file that is open without delete sharing.
        fixture.PosterFiles.Delete(PosterId);

        var sent = new byte[3];
        await beingSent!.ReadExactlyAsync(sent);
        Assert.Equal<byte>([1, 2, 3], sent);
        Assert.False(fixture.PosterFiles.Exists(PosterId));
    }
}
