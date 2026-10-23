using System.Globalization;
using System.Text;
using System.Text.Json;
using Weir.Infrastructure.IO;

namespace Weir.Infrastructure.Logging;

/// <summary>
/// The single JSON-lines runtime log, <c>{WEIR_LOG_DIR}/weir.log</c>. Writes and the retention
/// rewrite share one lock so the file is never replaced while a handle is open, which Windows
/// does not allow.
/// </summary>
/// <remarks>
/// A read of the whole log (the Logs screen) holds a read side of <see cref="_replacing"/> instead of the write lock, so every
/// lane keeps logging while it runs (#708); only a retention rewrite waits for readers to finish.
/// </remarks>
public sealed class WeirLogFile : IDisposable
{
    private readonly Lock _lock = new();
    private readonly ReaderWriterLockSlim _replacing = new();
    private readonly TimeProvider _time;
    private readonly IFileSystem _files;
    /// <summary>The append handle; null once disposed, and after a retention rewrite swapped the log in but could not open it again.</summary>
    private FileStream? _stream;
    private bool _disposed;

    public WeirLogFile(string path, TimeProvider time, IFileSystem? files = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = System.IO.Path.GetFullPath(path);
        _time = time;
        _files = files ?? PhysicalFileSystem.Instance;
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _stream = OpenForAppend(Path);
    }

    public string Path { get; }

    public void WriteLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (_lock)
        {
            if (_stream is null)
            {
                return;
            }

            // O_APPEND semantics: another writer (or a test) may have appended since the last write.
            _stream.Seek(0, SeekOrigin.End);
            _stream.Write(bytes);
            _stream.Flush();
        }
    }

    /// <summary>
    /// Keep only lines whose <c>timestamp</c> is within <paramref name="keepDays"/> (at least one day).
    /// Lines that are not JSON with a readable timestamp are dropped. Returns
    /// <see langword="false"/> when the file could not be rewritten; the log is left as it was.
    /// </summary>
    public bool Prune(int keepDays)
    {
        var cutoff = _time.GetUtcNow() - TimeSpan.FromDays(Math.Max(1, keepDays));
        if (!OldestLineAgedOut(cutoff))
        {
            return true;
        }

        _replacing.EnterWriteLock();
        try
        {
            return Rewrite(cutoff);
        }
        finally
        {
            _replacing.ExitWriteLock();
        }
    }

    /// <summary>
    /// Whether pruning would actually remove anything, read from the first line only: lines are always
    /// appended in order, so when even the oldest is within the window nothing else can be outside it either
    /// (#718). A line that cannot be read as JSON with a timestamp is what <see cref="Rewrite"/> drops first, so
    /// it counts as aged out too, and a file that cannot be opened is left for <see cref="Rewrite"/> to report.
    /// </summary>
    private bool OldestLineAgedOut(DateTimeOffset cutoff)
    {
        _replacing.EnterReadLock();
        try
        {
            string? firstLine;
            lock (_lock)
            {
                _stream?.Flush();
            }

            try
            {
                using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                firstLine = reader.ReadLine();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return true;
            }

            return firstLine is not null && (!TryReadTimestamp(firstLine, out var at) || at < cutoff);
        }
        finally
        {
            _replacing.ExitReadLock();
        }
    }

    /// <summary>
    /// The lines inside the window are written to a scratch file while writers keep appending; the write lock is taken only
    /// to carry over what was appended meanwhile and swap the scratch file in, so logging never waits for a copy of the
    /// whole log.
    /// </summary>
    private bool Rewrite(DateTimeOffset cutoff)
    {
        string? temporary = null;
        try
        {
            long length;
            lock (_lock)
            {
                if (!File.Exists(Path))
                {
                    ReopenForAppend();
                    return true;
                }

                _stream?.Flush();
                length = new FileInfo(Path).Length;
            }

            temporary = System.IO.Path.Join(
                System.IO.Path.GetDirectoryName(Path),
                $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.prune");
            var rewritten = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            try
            {
                using (var source = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(new BoundedReadStream(source, length), Encoding.UTF8))
                using (var writer = new StreamWriter(rewritten, new UTF8Encoding(false), leaveOpen: true))
                {
                    writer.NewLine = "\n";
                    while (reader.ReadLine() is { } raw)
                    {
                        if (TryReadTimestamp(raw, out var at) && at >= cutoff)
                        {
                            writer.WriteLine(raw.TrimEnd('\n'));
                        }
                    }
                }

                lock (_lock)
                {
                    try
                    {
                        _stream?.Dispose();
                        _stream = null;
                        using (var appended = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            appended.Seek(length, SeekOrigin.Begin);
                            appended.CopyTo(rewritten);
                        }

                        rewritten.Dispose();
                        _files.MoveFile(temporary, Path, overwrite: true);
                    }
                    finally
                    {
                        // Reopened before the lock is released: a writer that got in between would find no stream and drop its line.
                        ReopenForAppend();
                    }
                }
            }
            finally
            {
                rewritten.Dispose();
            }

            temporary = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (temporary is not null)
            {
                try
                {
                    _files.DeleteFile(temporary);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    // Best effort: a leftover temporary file does no harm.
                }
            }

            return false;
        }
    }

    /// <summary>Replace the append handle with a fresh one. The caller holds <see cref="_lock"/>.</summary>
    private void ReopenForAppend()
    {
        _stream?.Dispose();
        _stream = OpenForAppend(Path);
    }

    /// <summary>
    /// Read every line written so far. Writers keep appending while it reads; a prune waits until it is done, so the file is
    /// not replaced mid-read. Returns <see langword="false"/> when the file exists but could not be opened.
    /// </summary>
    public bool ReadLines(Action<string> onLine)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        _replacing.EnterReadLock();
        try
        {
            FileStream stream;
            long length;
            lock (_lock)
            {
                _stream?.Flush();
                try
                {
                    stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return false;
                }

                length = stream.Length;
            }

            using (stream)
            using (var reader = new StreamReader(new BoundedReadStream(stream, length), new UTF8Encoding(false)))
            {
                while (reader.ReadLine() is { } line)
                {
                    onLine(line);
                }
            }

            return true;
        }
        finally
        {
            _replacing.ExitReadLock();
        }
    }

    /// <summary>
    /// Copies the log to <paramref name="destinationPath"/> as a byte-for-byte snapshot, so a caller can hand it to
    /// something slow (a download to a browser) without holding up logging for as long as that takes. Writers keep
    /// appending while it copies, and the snapshot ends at the last whole line written when it began; a prune waits
    /// until it is done. A plain file copy is also far cheaper per line than <see cref="ReadLines"/>, which
    /// re-splits and re-allocates every line.
    /// A log that has not been written yet is not a failure: the destination is created empty. Returns
    /// <see langword="false"/> only when the log file exists but could not be read.
    /// </summary>
    public bool SnapshotTo(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        _replacing.EnterReadLock();
        try
        {
            FileStream source;
            long length;
            lock (_lock)
            {
                if (!File.Exists(Path))
                {
                    using (File.Create(destinationPath))
                    {
                    }

                    return true;
                }

                _stream?.Flush();
                try
                {
                    source = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return false;
                }

                length = source.Length;
            }

            try
            {
                using (source)
                using (var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    new BoundedReadStream(source, length).CopyTo(destination);
                }

                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                try
                {
                    _files.DeleteFile(destinationPath);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    // Best effort: a leftover empty or partial temporary file does no harm.
                }

                return false;
            }
        }
        finally
        {
            _replacing.ExitReadLock();
        }
    }

    /// <summary>Waits for a retention rewrite or a read already under way, so neither finds the log closed beneath it.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _replacing.EnterWriteLock();
        try
        {
            lock (_lock)
            {
                _stream?.Dispose();
                _stream = null;
            }
        }
        finally
        {
            _replacing.ExitWriteLock();
        }

        _replacing.Dispose();
    }

    internal static bool TryReadTimestamp(string raw, out DateTimeOffset timestamp)
    {
        timestamp = default;
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("timestamp", out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var text = value.GetString()!;
            return DateTimeOffset.TryParse(
                text.EndsWith('Z') ? text[..^1] + "+00:00" : text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out timestamp);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static FileStream OpenForAppend(string path) =>
        new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// The first <c>length</c> bytes of a file others keep appending to: what had been written, as whole lines, when the read
    /// began. A line still being appended is never read in half.
    /// </summary>
    internal sealed class BoundedReadStream(Stream inner, long length) : Stream
    {
        private long _remaining = length;

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
            if (_remaining <= 0)
            {
                return 0;
            }

            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
