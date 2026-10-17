using Weir.Core.Media;
using Weir.Core.Rules;

namespace Weir.Core.Tests.Media;

/// <summary>
/// "Other metadata" and "Language tags" are two choices a person makes separately. ffmpeg's way of clearing the file's other tags
/// (<c>-map_metadata -1</c>) erases every track's language tag with them, so the command writes the tags the plan keeps back.
/// </summary>
public sealed class OtherMetadataKeepsLanguagesTests
{
    private static RemuxPlan Plan(MetadataRules metadata) => new()
    {
        VideoIndices = [0],
        Audio = [new PlannedTrack { InputIndex = 1, LangLabel = "eng", Default = true, Kind = TrackKind.Audio }, new PlannedTrack { InputIndex = 2, LangLabel = "ger", Kind = TrackKind.Audio }],
        Subtitles = [new PlannedTrack { InputIndex = 3, LangLabel = "fre", Kind = TrackKind.Subtitle }, new PlannedTrack { InputIndex = 4, LangLabel = string.Empty, Kind = TrackKind.Subtitle }],
        Metadata = metadata,
    };

    private static IReadOnlyList<string> Argv(MetadataRules metadata) => FfmpegCommands.BuildRemuxArgv("ffmpeg", "in.mkv", "out.mkv", Plan(metadata));

    private static IEnumerable<string> LanguageWrites(IReadOnlyList<string> argv) =>
        argv.Zip(argv.Skip(1)).Where(pair => pair.Second.StartsWith("language=", StringComparison.Ordinal) && pair.Second.Length > "language=".Length).Select(pair => $"{pair.First} {pair.Second}");

    [Fact]
    public void Clearing_the_other_metadata_writes_back_the_language_of_every_track_that_has_one()
    {
        var argv = Argv(new MetadataRules { RemoveOtherMetadata = true });

        Assert.Equal(["-metadata:s:a:0 language=eng", "-metadata:s:a:1 language=ger", "-metadata:s:s:0 language=fre"], LanguageWrites(argv));
        Assert.True(argv.ToList().IndexOf("-map_metadata") < argv.ToList().IndexOf("-metadata:s:a:0"), "the tags go back after everything was cleared");
    }

    [Fact]
    public void Clearing_the_other_metadata_and_the_title_still_writes_the_languages_back()
    {
        var argv = Argv(new MetadataRules { RemoveOtherMetadata = true, RemoveTitle = true });

        Assert.Equal(3, LanguageWrites(argv).Count());
    }

    [Fact]
    public void Clearing_the_language_tags_as_well_writes_none_back() =>
        Assert.Empty(LanguageWrites(Argv(new MetadataRules { RemoveOtherMetadata = true, RemoveLanguageTags = true })));

    [Fact]
    public void Leaving_the_other_metadata_alone_writes_no_language_since_none_was_cleared() =>
        Assert.Empty(LanguageWrites(Argv(new MetadataRules { RemoveTitle = true })));
}
