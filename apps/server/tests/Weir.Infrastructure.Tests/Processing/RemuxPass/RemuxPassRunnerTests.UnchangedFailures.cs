using Weir.Core.Json;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>An unchanged file that cannot be placed in the output folder: it is failed in plain words, and what was there is left alone.</summary>
public sealed partial class RemuxPassRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unchanged_file_whose_output_name_is_a_folder_with_files_in_it_is_failed_and_the_source_stays(bool passThrough)
    {
        var source = _folders.Source(Path.Join("Film", "film.mkv"));
        var inTheWay = _folders.Out(Path.Join("Film", "film.mkv"));
        Directory.CreateDirectory(inTheWay);
        File.WriteAllText(Path.Join(inTheWay, "keep.txt"), "someone else's file");

        var result = await Run("Film/film.mkv", passThrough: passThrough);

        Assert.False(Bool(result, "ok"), WireJsonWriter.Dumps(result, WireJsonFormat.Compact));
        Assert.Equal(RemuxPassOutcomes.FailedDuringExecution, Str(result, "outcome"));
        Assert.False(string.IsNullOrWhiteSpace(Str(result, "reason")));
        Assert.False(Bool(result, "remux_required"));
        Assert.True(File.Exists(source));
        Assert.Equal("someone else's file", File.ReadAllText(Path.Join(inTheWay, "keep.txt")));
        Assert.Empty(Directory.GetFiles(_folders.Work));
        var last = _progress[^1];
        Assert.Equal("failed", Str(last, "status"));
        Assert.Equal("Weir could not copy this unchanged file to the output folder.", Str(last, "message"));
    }

    [Fact]
    public async Task An_unchanged_file_is_not_copied_over_its_own_source()
    {
        var source = _folders.Source("film.mkv", 3000);
        var runtime = new ProcessingPathRuntime { WatchedFolder = _folders.Watched, OutputFolder = _folders.Watched, WorkFolderEffective = _folders.Work, WorkFolderIsDefault = false };

        var result = await Run("film.mkv", runtime);

        Assert.False(Bool(result, "ok"), WireJsonWriter.Dumps(result, WireJsonFormat.Compact));
        Assert.True(File.Exists(source));
        Assert.Equal(3000, new FileInfo(source).Length);
    }
}
