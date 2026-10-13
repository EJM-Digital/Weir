using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Weir.Tray;

/// <summary>
/// What the server process says on its standard output and error, kept in <c>server-host.log</c> in the runtime home beside
/// tray-host.log. The server's own log, weir.log, only exists once the server has got that far; a server that stops before it, in
/// the runtime or the host that starts it, can say so only here. One file holds every server process the tray starts, each line
/// marked with the process id and the stream it came from. When it passes <see cref="DefaultMaxBytes"/> it becomes
/// <see cref="PreviousFileName"/>, replacing the one before, so the two never hold more than twice that.
/// </summary>
sealed class ServerOutputLog : IDisposable
{
    internal const string FileName = "server-host.log";
    internal const string PreviousFileName = "server-host.previous.log";
    internal const long DefaultMaxBytes = 1_000_000;

    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

    private readonly string _path;
    private readonly string _previousPath;
    private readonly long _maxBytes;
    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private long _length;

    internal ServerOutputLog(string runtimeHome, long maxBytes = DefaultMaxBytes)
    {
        _path = Path.Combine(runtimeHome, FileName);
        _previousPath = Path.Combine(runtimeHome, PreviousFileName);
        _maxBytes = maxBytes;
    }

    /// <summary>
    /// The encoding the server writes its console output in: the PC's OEM code page, which is the one a console the server was given
    /// uses. UTF-8 when this PC's cannot be had.
    /// </summary>
    internal static Encoding ServerEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>Keeps one line, with the time, as the tray's own words.</summary>
    internal void Write(string line) => Append($"[{DateTime.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture)}] {line}");

    /// <summary>
    /// Keeps everything <paramref name="process"/> writes from now until it closes its output. The process must have been
    /// started with both streams redirected. The streams are read to their end whatever happens to the file: a server whose
    /// output nobody reads stops at the first full pipe.
    /// </summary>
    internal void Attach(Process process, StartProgressFeed feed)
    {
        Write($"server pid {process.Id} started");
        BackgroundWork.Observe($"Server output (pid {process.Id}, out)", Pump(process.StandardOutput, process.Id, "out", feed));
        BackgroundWork.Observe($"Server output (pid {process.Id}, err)", Pump(process.StandardError, process.Id, "err", null));
    }

    // A progress line is progress, so it goes to the feed the start is judged by; the log keeps it in words, once for each phase and
    // step and not for each beat. Every other line is kept as the server wrote it.
    private async Task Pump(StreamReader reader, int processId, string stream, StartProgressFeed? feed)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (feed is not null && line.StartsWith(StartProgressFile.Prefix, StringComparison.Ordinal))
                {
                    if (feed.Accept(line) is { } record)
                    {
                        Write($"[pid {processId} progress] {record.Words}");
                    }

                    continue;
                }

                Write($"[pid {processId} {stream}] {line}");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process is gone, and so is its pipe.
        }
    }

    private void Append(string line)
    {
        lock (_gate)
        {
            try
            {
                _writer ??= Open();
                if (_length > _maxBytes)
                {
                    StartOver();
                    _writer = Open();
                }

                _writer.WriteLine(line);
                _length += Encoding.UTF8.GetByteCount(line) + 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // A log that cannot be written must not stop the reading of the pipe behind it.
                Trace.WriteLine($"Weir tray could not write {FileName} ({ex.Message}): {line}");
                _writer = null;
            }
        }
    }

    private StreamWriter Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _length = stream.Length;
        return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    private void StartOver()
    {
        _writer?.Dispose();
        _writer = null;
        File.Move(_path, _previousPath, overwrite: true);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
