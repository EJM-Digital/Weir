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

    [Fact]
    public async Task A_poster_replaced_while_it_is_being_sent_is_replaced_once_the_send_ends()
    {
        using var fixture = new ArtworkFixture();
        await fixture.PosterFiles.WriteAsync(PosterId, [1, 2, 3], CancellationToken.None);
        var beingSent = fixture.PosterFiles.Open(PosterId)!;
        var tries = 0;
        fixture.PosterFiles.Pause = (_, _) =>
        {
            tries++;
            beingSent.Dispose();
            return Task.CompletedTask;
        };

        var replaced = await fixture.PosterFiles.WriteAsync(PosterId, [4, 5, 6, 7], CancellationToken.None);

        Assert.True(replaced);
        Assert.Equal(1, tries);
        await using var stored = fixture.PosterFiles.Open(PosterId);
        Assert.Equal(4, stored!.Length);
        Assert.Empty(PartialFiles(fixture));
    }

    [Fact]
    public async Task A_poster_that_stays_open_is_kept_as_it_was_and_nothing_half_written_is_left()
    {
        using var fixture = new ArtworkFixture();
        await fixture.PosterFiles.WriteAsync(PosterId, [1, 2, 3], CancellationToken.None);
        await using var beingSent = fixture.PosterFiles.Open(PosterId);
        fixture.PosterFiles.Pause = (_, _) => Task.CompletedTask;

        var replaced = await fixture.PosterFiles.WriteAsync(PosterId, [4, 5, 6, 7], CancellationToken.None);

        Assert.False(replaced);
        var kept = new byte[3];
        await beingSent!.ReadExactlyAsync(kept);
        Assert.Equal<byte>([1, 2, 3], kept);
        Assert.Empty(PartialFiles(fixture));
    }

    private static string[] PartialFiles(ArtworkFixture fixture) =>
        Directory.GetFiles(Path.Join(fixture.Store.Options.WeirHome, "artwork", "posters"), "*.partial");
}
