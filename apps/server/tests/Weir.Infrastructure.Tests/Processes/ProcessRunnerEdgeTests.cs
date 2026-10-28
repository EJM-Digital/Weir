using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Weir.Infrastructure.Processes;

namespace Weir.Infrastructure.Tests.Processes;

/// <summary>The ways a tool's end and its pipes can go wrong around <see cref="ProcessRunner"/>, which a normal run does not reach.</summary>
public sealed class ProcessRunnerEdgeTests
{
    private static string StandInPath { get; } = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Weir.TestChild.exe" : "Weir.TestChild");

    private static Process StartHolding() =>
        Process.Start(new ProcessStartInfo(StandInPath, "hold") { UseShellExecute = false, CreateNoWindow = true })
        ?? throw new InvalidOperationException("The stand-in did not start.");

    private static async Task<Process> StartAndKillAsync()
    {
        var process = StartHolding();
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        return process;
    }

    [Fact]
    public async Task A_tool_still_running_has_no_exit_code_and_one_that_ended_has_its_own()
    {
        using var running = StartHolding();
        try
        {
            Assert.Equal(-1, ProcessRunner.ExitCodeOf(running));
        }
        finally
        {
            running.Kill(entireProcessTree: true);
            await running.WaitForExitAsync();
        }

        Assert.Equal(running.ExitCode, ProcessRunner.ExitCodeOf(running));
    }

    // The system's own refusal of a priority on an exited process is not repeatable (Windows accepts it about one time in eight), so
    // the refusals are handed in rather than waited for.
    public static TheoryData<Exception> Refusals => new()
    {
        new InvalidOperationException("Cannot process request because the process has exited."),
        new Win32Exception(5, "Access is denied."),
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_priority_that_cannot_be_set_is_logged_and_the_tool_keeps_running_at_its_own(Exception refusal)
    {
        var logger = new CapturingLogger<ProcessRunner>();
        using var process = new Process();

        new ProcessRunner(logger).SetPriority(process, ProcessPriorityClass.BelowNormal, "the stand-in", (_, _) => throw refusal);

        Assert.True(logger.Logged(LogLevel.Debug, "Could not set the priority of the stand-in"));
    }

    [Fact]
    public void A_priority_that_cannot_be_set_is_not_an_error_when_nothing_is_listening()
    {
        using var process = new Process();

        var thrown = Record.Exception(() => new ProcessRunner().SetPriority(process, ProcessPriorityClass.Normal, "the stand-in", (_, _) => throw new Win32Exception(5)));

        Assert.Null(thrown);
    }

    [Fact]
    public void A_priority_that_is_set_is_not_logged_and_reaches_the_process()
    {
        var logger = new CapturingLogger<ProcessRunner>();
        using var running = StartHolding();
        try
        {
            new ProcessRunner(logger).SetPriority(running, ProcessPriorityClass.BelowNormal, "the stand-in");

            Assert.Equal(ProcessPriorityClass.BelowNormal, running.PriorityClass);
            Assert.False(logger.Logged(LogLevel.Debug, "Could not set the priority"));
        }
        finally
        {
            running.Kill(entireProcessTree: true);
            running.WaitForExit();
        }
    }

    [Fact]
    public async Task Stopping_a_process_that_has_already_ended_is_not_an_error()
    {
        using var ended = await StartAndKillAsync();

        ProcessRunner.KillTree(ended);

        Assert.True(ended.HasExited);
    }

    [Fact]
    public void A_pipe_that_is_already_closed_ends_the_reading_with_what_was_read()
    {
        var sink = new ProcessRunner.OutputSink(ProcessOutput.Capture, 0);
        var closed = new MemoryStream();
        closed.Dispose();

        ProcessRunner.Pump(closed, sink, null, () => { });

        Assert.Empty(sink.ToArray());
    }

    [Fact]
    public void A_pipe_that_breaks_while_being_read_ends_the_reading_with_what_was_read()
    {
        var sink = new ProcessRunner.OutputSink(ProcessOutput.Capture, 0);
        var reads = 0;

        ProcessRunner.Pump(new BreakingStream([1, 2, 3]), sink, null, () => reads++);

        Assert.Equal([1, 2, 3], sink.ToArray());
        Assert.Equal(1, reads);
    }

    // Which side of the limit a tail ends on depends on how the pipe happened to chunk the child's output, so the sink is driven directly.
    // With a limit of 4 the sink trims when it holds more than 8 bytes, so each case below ends on a different side of those two numbers.
    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("abcd", "abcd")]
    [InlineData("abcdef", "cdef")]
    [InlineData("abcdefgh", "efgh")]
    [InlineData("abcdefghi", "fghi")]
    [InlineData("abcdefghijk", "hijk")]
    public void A_tail_keeps_only_the_last_bytes_on_either_side_of_its_limit(string written, string expected)
    {
        var sink = new ProcessRunner.OutputSink(ProcessOutput.Tail, 4);

        sink.Write(Encoding.UTF8.GetBytes(written));

        Assert.Equal(expected, Encoding.UTF8.GetString(sink.ToArray()));
    }

    [Fact]
    public void A_tail_written_in_pieces_keeps_the_last_bytes_across_a_trim()
    {
        var sink = new ProcessRunner.OutputSink(ProcessOutput.Tail, 4);

        sink.Write("abcdefghi"u8);
        sink.Write("jk"u8);

        Assert.Equal("hijk", Encoding.UTF8.GetString(sink.ToArray()));
    }

    [Fact]
    public void Discarded_output_is_not_kept()
    {
        var sink = new ProcessRunner.OutputSink(ProcessOutput.Discard, 4);

        sink.Write("abc"u8);

        Assert.Empty(sink.ToArray());
    }

    [Fact]
    public async Task A_pipe_kept_open_after_the_kill_is_given_up_on_after_the_limit()
    {
        using var ended = await StartAndKillAsync();
        var neverCloses = new TaskCompletionSource().Task;

        await ProcessRunner.DrainAsync(ended, neverCloses, Task.CompletedTask, TimeSpan.FromMilliseconds(50));

        Assert.False(neverCloses.IsCompleted);
    }

    [Fact]
    public async Task A_process_that_has_not_ended_after_the_limit_is_reported_through_HasExited()
    {
        using var running = StartHolding();
        try
        {
            await ProcessRunner.DrainAsync(running, Task.CompletedTask, Task.CompletedTask, TimeSpan.FromMilliseconds(50));

            Assert.False(running.HasExited);
        }
        finally
        {
            running.Kill(entireProcessTree: true);
            await running.WaitForExitAsync();
        }
    }

    private sealed class BreakingStream(byte[] first) : Stream
    {
        private bool _given;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_given)
            {
                throw new IOException("The pipe was closed by the other end.");
            }

            _given = true;
            first.CopyTo(buffer);
            return first.Length;
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
