using Microsoft.Extensions.Time.Testing;
using Weir.Core.Metrics;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

public sealed partial class RemuxPassRunnerTests
{
    [Fact]
    public async Task A_remux_that_made_the_file_smaller_adds_what_it_saved_to_the_server_figures()
    {
        var metrics = new RuntimeMetricsStore(new FakeTimeProvider());
        var source = _folders.Source("saving.mkv", bytes: 2000);
        _media.Probes["saving.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        _media.DefaultProbe = FakeMediaRunner.EnglishOnly;

        await Run("saving.mkv", runner: Runner(metrics: metrics));

        var written = new FileInfo(_folders.Out("saving.mkv")).Length;
        Assert.Equal(new FileInfo(source).Length - written, Assert.Single(metrics.GetSummary().ModuleSavingsBytes).Value);
    }

    [Fact]
    public async Task A_video_stream_that_names_no_codec_is_recorded_with_none()
    {
        _folders.Source("nameless.mkv");
        _media.Probes["nameless.mkv"] =
            """{"format":{"duration":"100.0"},"streams":[{"index":0,"codec_type":"video","width":1920,"height":1080,"duration":"100.0"},{"index":1,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"eng"},"disposition":{"default":1},"duration":"100.0"}]}""";

        await Run("nameless.mkv");

        Assert.Null(Assert.Single(_facts.Measured).VideoCodec);
    }

    [Fact]
    public async Task A_remux_that_did_not_make_the_file_smaller_adds_nothing_to_the_server_figures()
    {
        var metrics = new RuntimeMetricsStore(new FakeTimeProvider());
        _folders.Source("tiny.mkv", bytes: 10);
        _media.Probes["tiny.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        _media.DefaultProbe = FakeMediaRunner.EnglishOnly;

        await Run("tiny.mkv", runner: Runner(metrics: metrics));

        Assert.Empty(metrics.GetSummary().ModuleSavingsBytes);
    }
}
