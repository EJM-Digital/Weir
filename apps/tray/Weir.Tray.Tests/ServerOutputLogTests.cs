using System.Diagnostics;

using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// server-host.log never loses lines it could have kept and never holds on to what it should let go: a writer that failed is
/// disposed, a log that cannot be started over goes on growing and says so once, a log that has been disposed is not opened again, and
/// a server's pipes are closed when its output ends.
/// </summary>
public sealed class ServerOutputLogTests : IDisposable
{
    private readonly TempDirectory _home = TempDirectory.Create();

    public void Dispose() => _home.Dispose();

    private string LogPath => Path.Combine(_home.Path, ServerOutputLog.FileName);

    [Fact]
    public void A_log_that_has_been_disposed_is_not_opened_again_by_a_late_line()
    {
        var log = new ServerOutputLog(_home.Path);
        log.Write("first");
        log.Dispose();
        File.Delete(LogPath);

        log.Write("late line from a pipe that was still being read");

        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public void A_log_that_cannot_be_started_over_goes_on_growing_and_says_so_once()
    {
        var attempts = 0;
        using var log = new ServerOutputLog(_home.Path, maxBytes: 300)
        {
            MoveAside = (_, _) =>
            {
                attempts++;
                throw new IOException("The process cannot access the file because it is being used by another process.");
            },
        };

        for (var line = 0; line < 40; line++)
        {
            log.Write($"line {line:00} of what the server printed");
        }

        var text = ReadShared(LogPath);
        for (var line = 0; line < 40; line++)
        {
            Assert.Contains($"line {line:00} of what the server printed", text, StringComparison.Ordinal);
        }

        Assert.Equal(1, CountOf(text, "could not be started over"));
        // Not tried on every line: again only once the file has grown by the same again.
        Assert.InRange(attempts, 2, 10);
        Assert.False(File.Exists(Path.Combine(_home.Path, ServerOutputLog.PreviousFileName)));
    }

    [Fact]
    public void A_log_that_could_not_be_started_over_is_started_over_once_it_can_be()
    {
        var refusing = true;
        using var log = new ServerOutputLog(_home.Path, maxBytes: 300)
        {
            MoveAside = (from, to) =>
            {
                if (refusing)
                {
                    throw new IOException("busy");
                }

                File.Move(from, to, overwrite: true);
            },
        };
        for (var line = 0; line < 20; line++)
        {
            log.Write($"line {line:00} of what the server printed");
        }

        refusing = false;
        for (var line = 20; line < 80; line++)
        {
            log.Write($"line {line:00} of what the server printed");
        }

        Assert.True(File.Exists(Path.Combine(_home.Path, ServerOutputLog.PreviousFileName)));
        Assert.Contains("line 79", ReadShared(LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_writer_that_failed_is_disposed_and_the_next_line_opens_the_file_afresh()
    {
        var opened = new List<FailingOnceWriter>();
        using var log = new ServerOutputLog(_home.Path)
        {
            OpenWriter = path =>
            {
                var writer = new FailingOnceWriter(path, failFirstWrite: opened.Count == 0);
                opened.Add(writer);
                return writer.Writer;
            },
        };

        log.Write("this line meets the failure");
        log.Write("this line is written");

        Assert.Equal(2, opened.Count);
        Assert.True(opened[0].Disposed, "The writer that failed was kept");
        Assert.Contains("this line is written", ReadShared(LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_pipes_of_a_server_are_closed_when_its_output_ends()
    {
        using var log = new ServerOutputLog(_home.Path);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "whoami.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var standardOutput = process.StandardOutput;
        var standardError = process.StandardError;

        log.Attach(process, new StartProgressFeed(process.Id));
        await process.WaitForExitAsync();

        await StartRig.UntilAsync(() => !standardOutput.BaseStream.CanRead && !standardError.BaseStream.CanRead, "the pipes to be closed");
    }

    // The log is open for writing while a test reads it.
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static int CountOf(string text, string what)
    {
        var count = 0;
        for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // A writer over the real file whose first write throws, and that notes when it is disposed.
    private sealed class FailingOnceWriter
    {
        public FailingOnceWriter(string path, bool failFirstWrite)
        {
            var stream = new TrackingStream(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), failFirstWrite, () => Disposed = true);
            Writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }

        public StreamWriter Writer { get; }

        public bool Disposed { get; private set; }
    }

    private sealed class TrackingStream(Stream inner, bool failFirstWrite, Action disposed) : Stream
    {
        private bool _failNext = failFirstWrite;

        public override bool CanRead => false;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_failNext)
            {
                _failNext = false;
                throw new IOException("The disk is full.");
            }

            inner.Write(buffer, offset, count);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                disposed();
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
