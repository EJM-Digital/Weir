using Weir.Core.Activity;
using Weir.Core.Processing;
using Weir.Infrastructure.Tests.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>Choices of tracks that cannot be carried out are refused in plain words, and the file is left exactly as it was.</summary>
public sealed partial class LibraryCleanHandlerTests
{
    private async Task<(string Path, string EventType, string Title)> RunChosenAsync(ManualPlanChoice choice, string probe)
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = probe;

        await RunCleanAsync(await EnqueueChosenCleanAsync(library, path, choice));

        var title = await _fixture.Store.ScalarText("SELECT title FROM activity_events ORDER BY id DESC LIMIT 1");
        var type = await _fixture.Store.ScalarText("SELECT event_type FROM activity_events ORDER BY id DESC LIMIT 1");
        return (path, type!, title!);
    }

    [Fact]
    public async Task A_choice_naming_a_track_the_file_does_not_have_is_refused()
    {
        var (path, type, _) = await RunChosenAsync(Keeping(0, 9), FakeMediaRunner.EnglishAndJapanese);

        Assert.Equal(LibraryActivityEventTypes.FileFailed, type);
        Assert.Empty(_media.Remuxes);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_choice_that_keeps_every_track_in_order_has_nothing_to_do()
    {
        var (path, type, _) = await RunChosenAsync(Keeping(0, 1, 2), FakeMediaRunner.EnglishAndJapanese);

        Assert.Equal(LibraryActivityEventTypes.FileSkipped, type);
        Assert.Empty(_media.Remuxes);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_file_whose_track_list_cannot_be_read_is_failed_with_the_plain_reason()
    {
        var (path, type, _) = await RunChosenAsync(Keeping(0, 1), """{"format":{"duration":"100.0"},"streams":[{"index":0,"codec_type":5}]}""");

        Assert.Equal(LibraryActivityEventTypes.FileFailed, type);
        Assert.Empty(_media.Remuxes);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
    }
}
