using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Weir.Contract.FakeTools;

/// <summary>
/// ffmpeg: a capability query, an integrity read-through (<c>-f null</c>), or a remux. A remux writes
/// <c>FAKEMEDIA:</c> plus the source's probe restricted to the <c>-map</c>-ed streams, so the output validates exactly as the plan says.
/// </summary>
internal static class FakeFfmpegTool
{
    // A safety net for a test that forgot to release a remux it holds open: it should fail loudly, not hang the run.
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(120);

    public static int Run(ToolFolder folder, ToolScript script, string[] argv)
    {
        var source = ArgumentAfter(argv, "-i");
        if (source is null)
        {
            folder.LogCall("ffmpeg", argv, (FakeToolProtocol.StepKey, FakeToolProtocol.QueryStep));
            if (argv.Contains("-hwaccels"))
            {
                // A tool that is slow to start the first time it runs, as one is on a busy machine; later runs are quick.
                var startDelay = FakeFfprobe.Number(script.RuleFor(string.Empty), FakeToolProtocol.HwaccelsFirstCallDelaySecondsKey);
                if (startDelay > 0 && folder.Bump("hwaccels") == 1)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(startDelay));
                }

                Output.Out("Hardware acceleration methods:\n\n");
            }

            return 0;
        }

        var rule = script.RuleFor(source);
        var name = Path.GetFileName(source);
        if (argv.Contains("null") && ArgumentAfter(argv, "-f") == "null")
        {
            folder.LogCall("ffmpeg", argv, (FakeToolProtocol.StepKey, FakeToolProtocol.IntegrityStep), (FakeToolProtocol.FileKey, name));
            if (FakeFfprobe.Text(rule, FakeToolProtocol.IntegrityErrorKey) is { } integrityError)
            {
                Output.Error(integrityError);
                return 1;
            }

            return 0;
        }

        return Remux(folder, rule, argv, source, name);
    }

    private static int Remux(ToolFolder folder, JsonObject rule, string[] argv, string source, string name)
    {
        var attempt = folder.Bump($"remux:{name}");
        folder.LogCall("ffmpeg", argv, (FakeToolProtocol.StepKey, FakeToolProtocol.RemuxStep), (FakeToolProtocol.FileKey, name), (FakeToolProtocol.AttemptKey, attempt));
        var output = argv[^1];
        var delay = FakeFfprobe.Number(rule, FakeToolProtocol.RemuxDelaySecondsKey);
        var releaseFile = FakeFfprobe.Text(rule, FakeToolProtocol.RemuxReleaseFileKey);
        var progressSeconds = FakeFfprobe.Number(rule, FakeToolProtocol.RemuxProgressSecondsKey);
        if (progressSeconds > 0 && argv.Contains("-progress"))
        {
            Output.Out(string.Create(CultureInfo.InvariantCulture, $"out_time_ms={(long)(progressSeconds * 1_000_000)}\nprogress=continue\n"));
        }

        if (delay > 0 || releaseFile is not null)
        {
            File.WriteAllBytes(output, "partial fake output"u8.ToArray());
            if (delay > 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(delay));
            }

            if (releaseFile is not null && !WaitForRelease(releaseFile))
            {
                Output.Error($"{releaseFile} (remux_release_file) was never created within {ReleaseTimeout.TotalSeconds:0}s");
                return 1;
            }
        }

        var failTimes = rule[FakeToolProtocol.RemuxFailTimesKey] is JsonValue times && times.TryGetValue<double>(out var limit) ? (int?)limit : null;
        if (FakeFfprobe.Text(rule, FakeToolProtocol.RemuxErrorKey) is { } error && (failTimes is null || attempt <= failTimes))
        {
            Output.Error(error);
            return 1;
        }

        var probe = MediaProbe.For(rule, source);
        var streams = MappedStreams(probe, argv);
        Dispositions.Apply(streams, argv);
        var body = rule[FakeToolProtocol.OutputProbeKey] as JsonObject
            ?? new JsonObject
            {
                ["streams"] = new JsonArray(streams.Select(stream => (JsonNode)stream).ToArray()),
                ["format"] = (probe["format"] as JsonObject)?.DeepClone() ?? new JsonObject(),
            };
        File.WriteAllBytes(output, Encoding.UTF8.GetBytes(FakeToolProtocol.MediaMagic + body.ToJsonString()));
        if (argv.Contains("-progress"))
        {
            Output.Out("out_time_ms=0\nprogress=continue\nprogress=end\n");
        }

        return 0;
    }

    /// <summary>The source's streams in <c>-map 0:N</c> order, renumbered from zero, each with its own copy of its disposition.</summary>
    private static List<JsonObject> MappedStreams(JsonObject probe, string[] argv)
    {
        var byIndex = new Dictionary<int, JsonObject>();
        var position = 0;
        foreach (var stream in (probe["streams"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var index = stream["index"] is JsonValue value && value.TryGetValue<int>(out var declared) ? declared : position;
            byIndex[index] = stream;
            position++;
        }

        var streams = new List<JsonObject>();
        for (var i = 0; i < argv.Length - 1; i++)
        {
            if (argv[i] != "-map" || !argv[i + 1].StartsWith("0:", StringComparison.Ordinal))
            {
                continue;
            }

            if (!int.TryParse(argv[i + 1][2..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var oldIndex)
                || !byIndex.TryGetValue(oldIndex, out var original)
                || original.Count == 0)
            {
                continue;
            }

            var stream = (JsonObject)original.DeepClone();
            stream["index"] = streams.Count;
            stream["disposition"] = (original["disposition"] as JsonObject)?.DeepClone() ?? new JsonObject();
            streams.Add(stream);
        }

        return streams;
    }

    private static bool WaitForRelease(string path)
    {
        var deadline = DateTime.UtcNow + ReleaseTimeout;
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(50);
        }

        return true;
    }

    private static string? ArgumentAfter(string[] argv, string flag)
    {
        for (var i = 0; i < argv.Length - 1; i++)
        {
            if (argv[i] == flag)
            {
                return argv[i + 1];
            }
        }

        return null;
    }
}
