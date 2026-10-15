using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Json;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.Media;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// The output ownership policy (WEIR_CHOWN_OUTPUT, WEIR_DIR_MODE_OUTPUT) reaches every folder a pass creates to publish into, not
/// only the last one, whichever way the file is published: a copy of an unchanged file, or the output of a remux.
/// </summary>
public sealed class RemuxPassOutputOwnershipTests : IDisposable
{
    private sealed class RecordingOwnership : IOutputOwnership
    {
        public List<string> Directories { get; } = [];

        public List<string> Files { get; } = [];

        public void ApplyToFile(string path) => Files.Add(path);

        public void ApplyToDirectory(string path) => Directories.Add(path);
    }

    private readonly PassFolders _folders = new();
    private readonly FakeMediaRunner _media = new();
    private readonly RecordingOwnership _ownership = new();

    public void Dispose() => _folders.Dispose();

    private Task<WireObject> Run(string relative)
    {
        var resolver = new FixedResolver();
        var runner = new RemuxPassRunner(
            new MediaTools(_media, resolver, new ListLogger<MediaTools>(), TimeProvider.System),
            resolver,
            new RecordingFacts(),
            new FakeCleanupData(),
            new SkippedTvSeasonFolderCleanup(),
            new FakeOriginalLanguage(),
            new RemuxPassSettings { MovieOutputCleanupMinAgeSeconds = 0, TvOutputCleanupMinAgeSeconds = 0 },
            TimeProvider.System,
            NullLogger<RemuxPassRunner>.Instance,
            ownership: _ownership)
        {
            HardlinkFastPathSupported = false,
        };
        return runner.RunAsync(new RemuxPassRequest
        {
            Runtime = _folders.Runtime(),
            RelativeMediaPath = relative,
            MinInputFileSizeMb = 0,
            MediaScope = "movie",
            MinFileAgeSeconds = 0,
            CurrentJobId = 1,
        });
    }

    private string[] Folders(params string[] relative) => relative.Select(folder => Path.GetFullPath(_folders.Out(folder))).ToArray();

    [Fact]
    public async Task Every_folder_made_to_copy_an_unchanged_file_into_is_given_the_policy()
    {
        _folders.Source(Path.Join("Show", "Season 1", "one.mkv"));

        var result = await Run("Show/Season 1/one.mkv");

        Assert.True(result.Get("ok") is WireBool { Value: true }, WireJsonWriter.Dumps(result, WireJsonFormat.Compact));
        Assert.Equal("validated_copy", WireConvert.Str(result["unchanged_output_method"]));
        Assert.Equal(Folders("Show", Path.Join("Show", "Season 1")), _ownership.Directories);
        Assert.Equal([Path.GetFullPath(_folders.Out(Path.Join("Show", "Season 1", "one.mkv")))], _ownership.Files);
    }

    [Fact]
    public async Task Every_folder_made_to_publish_a_remux_into_is_given_the_policy()
    {
        _folders.Source(Path.Join("Show", "Season 1", "two.mkv"));
        _media.Probes["two.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        _media.DefaultProbe = FakeMediaRunner.EnglishOnly;

        var result = await Run("Show/Season 1/two.mkv");

        Assert.True(result.Get("ok") is WireBool { Value: true }, WireJsonWriter.Dumps(result, WireJsonFormat.Compact));
        Assert.Single(_media.Remuxes);
        Assert.Equal(Folders("Show", Path.Join("Show", "Season 1")), _ownership.Directories);
        Assert.Equal([Path.GetFullPath(_folders.Out(Path.Join("Show", "Season 1", "two.mkv")))], _ownership.Files);
    }

    [Fact]
    public async Task A_folder_that_was_already_there_is_left_as_it_is()
    {
        Directory.CreateDirectory(_folders.Out("Show"));
        _folders.Source(Path.Join("Show", "Season 1", "three.mkv"));

        await Run("Show/Season 1/three.mkv");

        Assert.Equal(Folders(Path.Join("Show", "Season 1")), _ownership.Directories);
    }
}
