using System.Text.Json;
using Microsoft.Extensions.Logging;
using Weir.Core.Media;
using Weir.Core.Rules;
using Weir.Infrastructure.Media;

namespace Weir.Infrastructure.Tests.Media;

/// <summary>The staged remux when the writer fails and the file it leaves cannot be removed, and when the kept streams cannot be timed.</summary>
public sealed class MediaToolsRemuxEdgeTests : IDisposable
{
    private static readonly RemuxPlan Plan = new() { VideoIndices = [0], Audio = [], Subtitles = [] };

    private readonly TempDirectory _folder = new();
    private readonly ListLogger<MediaTools> _log = new();

    public void Dispose() => _folder.Dispose();

    private MediaTools Tools(ScriptedRunner? runner = null) =>
        new(runner ?? new ScriptedRunner(_ => new ScriptedRun()), new FixedResolver(), _log, TimeProvider.System, path => new MediaFileState(path, true, true, 100, 0));

    private Task<StagedRemux> RemuxAsync(string source, IRemuxWriter writer) =>
        Tools().RemuxToTempFileAsync(source, _folder.Join("work"), Plan, JsonDocument.Parse("{}").RootElement, [], writer: writer);

    [Fact]
    public async Task A_source_without_an_extension_is_staged_as_a_matroska_file_and_a_cancelled_write_leaves_nothing()
    {
        var writer = new FailingWriter();

        await Assert.ThrowsAsync<OperationCanceledException>(() => RemuxAsync(_folder.Join("Film"), writer));

        Assert.EndsWith(".mkv", writer.Destination, StringComparison.Ordinal);
        Assert.StartsWith("Film.processing.", Path.GetFileName(writer.Destination), StringComparison.Ordinal);
        Assert.False(File.Exists(writer.Destination));
    }

    [Fact]
    public async Task A_staged_name_another_writer_took_first_is_left_alone_and_another_name_is_drawn()
    {
        var taken = string.Empty;
        var files = new FaultingFileSystem()
            .Before(
                FileCall.OpenFile,
                path =>
                {
                    taken = path;
                    File.WriteAllText(path, "someone else's");
                },
                pathEndsWith: ".mkv")
            .Fail(FileCall.OpenFile, _ => new IOException("The file exists."), pathEndsWith: ".mkv");
        var writer = new FailingWriter();
        var tools = new MediaTools(new ScriptedRunner(_ => new ScriptedRun()), new FixedResolver(), _log, TimeProvider.System, path => new MediaFileState(path, true, true, 100, 0)) { Files = files };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => tools.RemuxToTempFileAsync(_folder.Join("Film.mkv"), _folder.Join("work"), Plan, JsonDocument.Parse("{}").RootElement, [], writer: writer));

        Assert.NotEqual(string.Empty, taken);
        Assert.NotEqual(taken, writer.Destination);
        Assert.Equal("someone else's", File.ReadAllText(taken));
        Assert.False(File.Exists(writer.Destination));
    }

    [WindowsFact("A read-only file refuses to be deleted by name only on Windows")]
    public async Task A_staged_file_that_cannot_be_removed_after_a_cancelled_write_is_logged_and_the_cancellation_is_still_what_is_raised()
    {
        var writer = new FailingWriter { HoldTheFile = true };

        await Assert.ThrowsAsync<OperationCanceledException>(() => RemuxAsync(_folder.Join("Film.mkv"), writer));

        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Could not remove temp file", StringComparison.Ordinal));
        Assert.True(File.Exists(writer.Destination));
        File.SetAttributes(writer.Destination, FileAttributes.Normal);
    }

    [Fact]
    public async Task A_staged_output_is_refused_when_nothing_says_how_long_the_kept_streams_should_run()
    {
        var error = await Assert.ThrowsAsync<MediaCompletenessException>(
            () => Tools().ValidateStagedOutputAsync(_folder.Join("out.mkv"), _folder.Join("Film.mkv"), JsonDocument.Parse("{}").RootElement, Plan, []));

        Assert.Contains("could not establish how long the kept streams should run", error.Message, StringComparison.Ordinal);
    }

    private sealed class FailingWriter : IRemuxWriter
    {
        public string Name => "the failing writer";

        public string Destination { get; private set; } = string.Empty;

        public bool HoldTheFile { get; init; }

        public bool CanWrite(string destination) => true;

        public Task WriteAsync(RemuxWriteRequest request, CancellationToken cancellationToken = default)
        {
            Destination = request.Destination;
            if (HoldTheFile)
            {
                File.SetAttributes(request.Destination, FileAttributes.ReadOnly);
            }

            throw new OperationCanceledException("The write was cancelled.");
        }
    }
}
