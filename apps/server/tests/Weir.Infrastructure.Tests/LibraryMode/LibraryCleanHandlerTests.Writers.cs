using System.Text.Json;
using Weir.Core.Activity;
using Weir.Core.Media;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.Tests.Media;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>Which tool wrote a cleaned file and what is said about it, and a file whose total length ffprobe does not give.</summary>
public sealed partial class LibraryCleanHandlerTests
{
    private const string StreamsWithoutAFormatLength =
        """{"streams":[{"index":0,"codec_type":"video","codec_name":"h264","duration":"100.0"},{"index":1,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"eng"},"disposition":{"default":1},"duration":"100.0"},{"index":2,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"jpn"},"duration":"100.0"}]}""";

    [Theory]
    [InlineData(RemuxWriterChoice.Best, true, "Weir wrote it with ffmpeg because mkvmerge could not.")]
    [InlineData(RemuxWriterChoice.Ffmpeg, true, null)]
    [InlineData(RemuxWriterChoice.Best, false, null)]
    public void The_fallback_to_ffmpeg_is_mentioned_only_when_mkvmerge_was_wanted_and_ffmpeg_wrote_the_file(string choice, bool ffmpegWrote, string? note)
    {
        var tools = new MediaTools(_media, new FixedResolver(mkvmerge: "mkvmerge"), new ListLogger<MediaTools>(), TimeProvider.System);
        IRemuxWriter writer = ffmpegWrote ? new FfmpegRemuxWriter(tools) : new MkvmergeRemuxWriter(tools, new FixedResolver(mkvmerge: "mkvmerge"));

        Assert.Equal(note, LibraryCleanHandler.FfmpegFallbackNote(choice, new StagedRemux("staged.mkv", writer)));
    }

    [Fact]
    public async Task A_file_whose_probe_gives_no_total_length_is_still_cleaned_from_the_length_of_its_streams()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = StreamsWithoutAFormatLength;
        using var probed = JsonDocument.Parse(StreamsWithoutAFormatLength);
        Assert.False(probed.RootElement.TryGetProperty("format", out _));

        await RunCleanAsync(await EnqueueCleanAsync(library, path, confirmFinalRemoval: true));

        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM activity_events WHERE event_type = '{LibraryActivityEventTypes.FileCleaned}'"));
    }
}
