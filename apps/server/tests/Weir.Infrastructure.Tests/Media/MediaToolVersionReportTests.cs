using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Weir.Core.Media;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.Processes;

namespace Weir.Infrastructure.Tests.Media;

/// <summary>
/// #548: <see cref="MediaTools.DescribeVersionsAsync"/>, the data behind <c>GET /api/v1/system/media-tools</c>.
/// The point of that endpoint is to describe a *broken* install, so these tests are mostly about the ways an
/// answer cannot be had — and every one of them has to come back as a string in the report rather than as an
/// exception the endpoint would turn into a 500.
/// </summary>
public sealed class MediaToolVersionReportTests
{
    private static MediaTools Tools(IMediaToolResolver resolver, Func<ProcessRequest, ScriptedRun> script) =>
        new(new ScriptedRunner(script), resolver, new ListLogger<MediaTools>(), TimeProvider.System);

    private static ScriptedRun Printing(string text) =>
        new() { ExitCode = 0, Stdout = Encoding.UTF8.GetBytes(text) };

    [Fact]
    public async Task Both_tools_report_the_version_they_printed()
    {
        var tools = Tools(
            new FixedResolver(mkvmerge: "mkvmerge"),
            request => request.Argv[0] == "mkvmerge"
                ? Printing("mkvmerge v102.0 ('Neon Lights') 64-bit\n")
                : Printing("ffmpeg version 8.0 Copyright (c) 2000-2025 the FFmpeg developers\nconfiguration: --enable-gpl\n"));

        var report = await tools.DescribeVersionsAsync();

        Assert.Equal("ffmpeg version 8.0 Copyright (c) 2000-2025 the FFmpeg developers", report.Ffmpeg);
        Assert.Equal("mkvmerge v102.0 ('Neon Lights') 64-bit", report.Mkvmerge);
    }

    [Fact]
    public async Task A_missing_mkvmerge_is_not_installed_and_is_never_asked_for_a_version()
    {
        // The whole reason for the endpoint: mkvmerge is optional, so this is the normal answer on a source
        // install that never added it, and it must not read as a failure. The resolver returning null is the
        // only signal there is — nothing is executed.
        var runner = new ScriptedRunner(_ => Printing("ffmpeg version 8.0\n"));
        var tools = new MediaTools(runner, new FixedResolver(), new ListLogger<MediaTools>(), TimeProvider.System);

        var report = await tools.DescribeVersionsAsync();

        Assert.Equal(MediaToolVersions.NotInstalled, report.Mkvmerge);
        Assert.Equal("ffmpeg version 8.0", report.Ffmpeg);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task A_missing_ffmpeg_reports_not_installed_instead_of_throwing()
    {
        // MediaToolResolver.Resolve throws for a missing ffmpeg, because everywhere else in Weir that *is* an
        // error. Here it is the answer the operator came for, so it is caught and reported.
        var tools = Tools(new ThrowingResolver(), _ => Printing("unreachable"));

        var report = await tools.DescribeVersionsAsync();

        Assert.Equal(MediaToolVersions.NotInstalled, report.Ffmpeg);
        Assert.Equal(MediaToolVersions.NotInstalled, report.Mkvmerge);
    }

    [Fact]
    public async Task A_tool_that_is_on_disk_but_will_not_run_reports_unknown_not_not_installed()
    {
        // "The file is there but broken" (wrong architecture, partial download, no execute permission) and
        // "there is no file" send an operator to different places, so they never collapse into one answer.
        var tools = Tools(
            new FixedResolver(mkvmerge: "mkvmerge"),
            request => request.Argv[0] == "mkvmerge"
                ? new ScriptedRun { Throw = new Win32Exception("%1 is not a valid Win32 application") }
                : new ScriptedRun { ExitCode = 1, Stderr = Encoding.UTF8.GetBytes("Unrecognized option '-version'\n") });

        var report = await tools.DescribeVersionsAsync();

        Assert.Equal(MediaToolVersions.Unknown, report.Ffmpeg);
        Assert.Equal(MediaToolVersions.Unknown, report.Mkvmerge);
    }

    [Fact]
    public async Task A_tool_that_hangs_reports_unknown_rather_than_holding_the_request_open()
    {
        var tools = Tools(
            new FixedResolver(mkvmerge: "mkvmerge"),
            _ => new ScriptedRun { Timeout = ProcessTimeoutKind.Overall });

        var report = await tools.DescribeVersionsAsync();

        Assert.Equal(MediaToolVersions.Unknown, report.Ffmpeg);
        Assert.Equal(MediaToolVersions.Unknown, report.Mkvmerge);
    }

    [Fact]
    public async Task An_older_ffmpeg_that_writes_its_banner_to_stderr_is_still_read()
    {
        var tools = Tools(
            new FixedResolver(),
            _ => new ScriptedRun { ExitCode = 0, Stderr = Encoding.UTF8.GetBytes("ffmpeg version 4.4.2-0ubuntu0.22.04.1\n") });

        var report = await tools.DescribeVersionsAsync();

        Assert.Equal("ffmpeg version 4.4.2-0ubuntu0.22.04.1", report.Ffmpeg);
    }

    [Fact]
    public async Task Each_tool_is_asked_with_its_own_version_argv()
    {
        var runner = new ScriptedRunner(_ => Printing("v\n"));
        var tools = new MediaTools(runner, new FixedResolver(ffmpeg: "/usr/bin/ffmpeg", mkvmerge: "/usr/bin/mkvmerge"), new ListLogger<MediaTools>(), TimeProvider.System);

        await tools.DescribeVersionsAsync();

        Assert.Equal(["/usr/bin/ffmpeg", "-hide_banner", "-version"], runner.Requests[0].Argv);
        Assert.Equal(["/usr/bin/mkvmerge", "--version"], runner.Requests[1].Argv);
    }

    [Fact]
    public async Task Both_version_questions_run_at_normal_priority_with_the_measured_time_limit()
    {
        var runner = new ScriptedRunner(_ => Printing("v\n"));
        var tools = new MediaTools(runner, new FixedResolver(mkvmerge: "mkvmerge"), new ListLogger<MediaTools>(), TimeProvider.System);

        await tools.DescribeVersionsAsync();

        Assert.Equal(2, runner.Requests.Count);
        Assert.All(runner.Requests, request =>
        {
            Assert.Equal(ProcessPriorityClass.Normal, request.Priority);
            Assert.Equal(TimeSpan.FromSeconds(15), request.Timeout);
        });
    }

    [Fact]
    public async Task Tools_starved_of_the_processor_are_not_reported_as_unknown()
    {
        // Below normal priority on a saturated machine, a version question gets the processor only in short turns and
        // outlasts its limit; at normal priority it answers. The fake answers only to a request that asks for normal.
        var tools = Tools(
            new FixedResolver(mkvmerge: "mkvmerge"),
            request => request.Priority != ProcessPriorityClass.Normal
                ? new ScriptedRun { Timeout = ProcessTimeoutKind.Overall }
                : request.Argv[0] == "mkvmerge"
                    ? Printing("mkvmerge v102.0 ('Neon Lights') 64-bit\n")
                    : Printing("ffmpeg version 8.0\n"));

        var report = await tools.DescribeVersionsAsync();

        Assert.Equal("ffmpeg version 8.0", report.Ffmpeg);
        Assert.Equal("mkvmerge v102.0 ('Neon Lights') 64-bit", report.Mkvmerge);
    }

    private sealed class ThrowingResolver : IMediaToolResolver
    {
        public (string Ffprobe, string Ffmpeg) Resolve() => throw new MediaToolException(MediaToolLocations.MissingToolsMessage);

        public string? ResolveMkvmerge() => null;
    }
}
