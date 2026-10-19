using Weir.Core.Json;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>The size checks an output must pass before its source may be removed, on real files.</summary>
public sealed class OutputCompletenessTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Written(string name, int bytes)
    {
        var path = _dir.Join(name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static (string Check, string? Note) Check(string output, string source, bool tv = false)
    {
        var result = RemuxPassRunner.CheckOutputFileCompleteness(output, source, tv);
        return (WireConvert.Str(result["output_completeness_check"]), result["output_completeness_note"] is WireString note ? note.Value : null);
    }

    [Fact]
    public void An_output_a_hundredth_of_the_source_or_more_passes()
    {
        Assert.Equal(("passed", (string?)null), Check(Written("out.mkv", 100), Written("src.mkv", 10_000)));
    }

    [Fact]
    public void An_output_that_is_not_there_fails()
    {
        Assert.Equal(("failed", "The output file is missing at the expected path."), Check(_dir.Join("missing.mkv"), Written("src.mkv", 100)));
    }

    [Fact]
    public void An_empty_output_fails()
    {
        Assert.Equal(("failed", "The output file is empty (zero bytes)."), Check(Written("out.mkv", 0), Written("src.mkv", 100)));
    }

    [Fact]
    public void A_source_that_can_no_longer_be_read_fails_with_the_system_reason()
    {
        var (check, note) = Check(Written("out.mkv", 10), _dir.Join("gone.mkv"));

        Assert.Equal("failed", check);
        Assert.StartsWith("Weir could not read the file size (", note, StringComparison.Ordinal);
    }

    [Fact]
    public void An_output_far_smaller_than_the_source_fails_in_the_words_of_the_kind_of_cleanup()
    {
        var output = Written("out.mkv", 50);
        var source = Written("src.mkv", 10_000);

        Assert.Equal(
            "The output file is much smaller than the source (under 1% of source size), so Weir skipped removing the release folder as a safety step.",
            Check(output, source).Note);
        Assert.Equal(
            "The output file is much smaller than the source (under 1% of source size), so Weir blocked TV season cleanup as a safety step.",
            Check(output, source, tv: true).Note);
    }

    [Fact]
    public void A_source_of_no_size_cannot_make_an_output_too_small()
    {
        Assert.Equal("passed", Check(Written("out.mkv", 1), Written("src.mkv", 0)).Check);
    }
}
