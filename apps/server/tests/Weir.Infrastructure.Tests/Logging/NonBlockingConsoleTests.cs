using System.Text;
using Microsoft.Extensions.Logging;
using Weir.Infrastructure.Logging;

namespace Weir.Infrastructure.Tests.Logging;

/// <summary>
/// Standard output is written by a thread of its own and never waited for. A pipe nobody reads takes a few kilobytes and then makes a
/// write wait for ever, so no log call may wait on the console (ServerOutputPipeTests starts the real server on such a pipe). Nothing here depends on how long anything takes: a console that will not take a line is a stream that does not return.
/// </summary>
public sealed class NonBlockingConsoleTests
{
    private const int TestTimeoutMs = 30_000;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Lines_reach_the_console_in_order_in_the_encoding_it_was_given()
    {
        using var stream = new RecordingStream();
        using var console = new NonBlockingConsole(stream, Encoding.UTF8);

        console.WriteLine("first");
        console.WriteLine("second é");
        await stream.WaitForTextAsync("second é");

        Assert.Equal($"first{Environment.NewLine}second é{Environment.NewLine}", stream.Text);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_console_that_never_takes_a_line_never_makes_the_caller_wait()
    {
        using var stream = new BlockedStream();
        using var console = new NonBlockingConsole(stream, Encoding.UTF8);
        await stream.BlockTheWriterAsync(console);

        // Far more than the queue holds. Were any call to wait on the console the caller would never get back.
        await Task.Run(() =>
        {
            for (var line = 0; line < NonBlockingConsole.Capacity * 5; line++)
            {
                console.WriteLine($"line {line}");
            }
        }).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(console.Dropped >= NonBlockingConsole.Capacity * 3, $"Only {console.Dropped} lines were dropped");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Once_the_console_takes_lines_again_it_is_told_how_many_were_not_written()
    {
        using var stream = new BlockedStream();
        using var console = new NonBlockingConsole(stream, Encoding.UTF8);
        await stream.BlockTheWriterAsync(console);
        for (var line = 0; line < NonBlockingConsole.Capacity * 2; line++)
        {
            console.WriteLine($"line {line}");
        }

        var dropped = console.Dropped;
        stream.Release();
        Assert.True(console.Drain(TimeSpan.FromSeconds(20)), "The console never took what was queued");
        console.WriteLine("after");
        await stream.WaitForTextAsync("after");

        Assert.Contains("console lines were not written because nothing was reading standard output", stream.Text, StringComparison.Ordinal);
        Assert.Contains("(" + dropped, stream.Text, StringComparison.Ordinal);
        Assert.True(stream.Text.IndexOf("were not written", StringComparison.Ordinal) < stream.Text.IndexOf("after", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_console_that_has_closed_is_not_an_error_for_the_caller()
    {
        using var console = new NonBlockingConsole(new ClosedStream(), Encoding.UTF8);

        await Task.Run(() =>
        {
            for (var line = 0; line < NonBlockingConsole.Capacity * 2; line++)
            {
                console.WriteLine($"line {line}");
            }
        }).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(console.Drain(TimeSpan.FromSeconds(10)), "Lines to a closed console were never given up on");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Draining_waits_for_what_was_handed_over_and_gives_up_on_a_console_that_will_not_take_it()
    {
        using var open = new RecordingStream();
        using var openConsole = new NonBlockingConsole(open, Encoding.UTF8);
        openConsole.WriteLine("last words");
        Assert.True(openConsole.Drain(TimeSpan.FromSeconds(10)));
        await open.WaitForTextAsync("last words");

        using var blocked = new BlockedStream();
        using var blockedConsole = new NonBlockingConsole(blocked, Encoding.UTF8);
        blockedConsole.WriteLine("never read");

        Assert.False(blockedConsole.Drain(TimeSpan.FromMilliseconds(200)));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task The_logger_writes_weirs_plain_line_at_and_above_the_level_and_never_waits_for_the_console()
    {
        using var stream = new RecordingStream();
        using var console = new NonBlockingConsole(stream, Encoding.UTF8);
        using var provider = new WeirConsoleLoggerProvider(console, TimeProvider.System, LogLevel.Information);
        var logger = provider.CreateLogger("Weir.Host.Startup");

        logger.LogDebug("not at this level");
        logger.LogInformation("Opening the database");
        logger.LogWarning("Something to know");
        await stream.WaitForTextAsync("Something to know");

        Assert.DoesNotContain("not at this level", stream.Text, StringComparison.Ordinal);
        Assert.Contains("INFO Weir.Host.Startup Opening the database", stream.Text, StringComparison.Ordinal);
        Assert.Contains("WARN", stream.Text, StringComparison.Ordinal);
        Assert.True(logger.IsEnabled(LogLevel.Error));
        Assert.False(logger.IsEnabled(LogLevel.Trace));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task The_logger_keeps_going_when_the_console_is_blocked()
    {
        using var stream = new BlockedStream();
        using var console = new NonBlockingConsole(stream, Encoding.UTF8);
        using var provider = new WeirConsoleLoggerProvider(console, TimeProvider.System, LogLevel.Information);
        var logger = provider.CreateLogger("Weir.Host.Startup");
        await stream.BlockTheWriterAsync(console);

        await Task.Run(() =>
        {
            for (var line = 0; line < NonBlockingConsole.Capacity * 3; line++)
            {
                logger.LogInformation("line {Number}", line);
            }
        }).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(console.Dropped > 0);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task The_count_of_dropped_lines_is_a_running_total_that_telling_the_console_does_not_reset()
    {
        using var stream = new BlockedStream();
        using var console = new NonBlockingConsole(stream, Encoding.UTF8);
        await stream.BlockTheWriterAsync(console);
        for (var line = 0; line < NonBlockingConsole.Capacity * 3; line++)
        {
            console.WriteLine($"line {line}");
        }

        var droppedBefore = console.Dropped;
        stream.Release();
        Assert.True(console.Drain(TimeSpan.FromSeconds(20)), "The console never took what was queued");

        Assert.True(droppedBefore >= NonBlockingConsole.Capacity * 2, $"Only {droppedBefore} lines were counted as dropped");
        Assert.Equal(droppedBefore, console.Dropped);
        Assert.Contains($"({droppedBefore} console lines were not written", stream.Text, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Stopping_gives_what_is_queued_time_to_reach_a_console_that_is_taking_it()
    {
        using var stream = new RecordingStream();
        var console = new NonBlockingConsole(stream, Encoding.UTF8);
        for (var line = 0; line < 500; line++)
        {
            console.WriteLine($"line {line}");
        }

        console.Dispose();

        // Nothing is waited for here: Dispose returned only after the writer had written them all.
        Assert.EndsWith($"line 499{Environment.NewLine}", stream.Text, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Stopping_does_not_wait_for_ever_for_a_console_that_will_not_take_a_line()
    {
        using var stream = new BlockedStream();
        var console = new NonBlockingConsole(stream, Encoding.UTF8);
        await stream.BlockTheWriterAsync(console);

        await Task.Run(console.Dispose).WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed class RecordingStream : Stream
    {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return _text.ToString();
                }
            }
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public async Task WaitForTextAsync(string text)
        {
            while (!Text.Contains(text, StringComparison.Ordinal))
            {
                await Task.Delay(5, CancellationToken.None);
            }
        }

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                _text.Append(Encoding.UTF8.GetString(buffer, offset, count));
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // A pipe that is full and not read: a write does not return until the test lets it.
    private sealed class BlockedStream : Stream
    {
        private readonly ManualResetEventSlim _open = new(false);
        private readonly ManualResetEventSlim _writing = new(false);
        private readonly RecordingStream _inner = new();

        public string Text => _inner.Text;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Release() => _open.Set();

        /// <summary>
        /// Hands the console one line and waits until the writing thread is stuck inside this stream with it. The console takes the count
        /// of dropped lines for its notice when its thread first gets a line, so a test that counts drops must not start until that thread
        /// is running: on a busy machine the thread can start after thousands of lines were dropped, and the count they made goes into a
        /// notice that this stream is holding.
        /// </summary>
        public async Task BlockTheWriterAsync(NonBlockingConsole console)
        {
            console.WriteLine("the line the writer is stuck on");
            await Task.Run(() => _writing.Wait()).WaitAsync(TimeSpan.FromSeconds(20));
        }

        public Task WaitForTextAsync(string text) => _inner.WaitForTextAsync(text);

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _writing.Set();
            _open.Wait();
            _inner.Write(buffer, offset, count);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // A write still waiting is let go so the thread ends with the test.
            _open.Set();
            base.Dispose(disposing);
        }
    }

    private sealed class ClosedStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("The pipe is being closed.");

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
