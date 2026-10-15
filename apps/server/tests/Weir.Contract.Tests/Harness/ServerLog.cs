using System.Diagnostics;

namespace Weir.Contract.Tests.Harness;

/// <summary>Everything one server process wrote to its output and error streams, kept in a file next to its data.</summary>
public sealed class ServerLog : IDisposable
{
    private const int TailLines = 40;

    /// <summary>
    /// How much of the log a server that exited before it was ready shows. Its log is only its start, and a crash prints the reason
    /// above a stack trace, or twice, which is more than <see cref="TailLines"/> lines.
    /// </summary>
    private const int StartFailureLines = 200;

    private readonly object _writeLock = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    public ServerLog(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        // Shared for reading, so a failing test can show the log while the server still writes to it.
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public string Path { get; }

    /// <summary>
    /// Completes when both of the process's streams have ended and everything they held is in the log. It never ends for a process
    /// that is still running, and may not for one whose child keeps a stream open after it has exited.
    /// </summary>
    public Task OutputEnded { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Copies the process's output and error lines into the log. Each stream is read by a thread of its own: on Windows a read of
    /// a redirected pipe holds its thread until the process ends, wherever it is started, and a thread-pool thread held that way
    /// is one less for everything else in the test process (see <see cref="ServerLogTests"/>).
    /// </summary>
    public void Follow(Process process) =>
        OutputEnded = Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));

    /// <summary>Adds a line of the harness's own, such as why the server was started again.</summary>
    public void Note(string text) => Append(text);

    private Task Pump(StreamReader reader) =>
        Task.Factory.StartNew(
            () =>
            {
                try
                {
                    while (reader.ReadLine() is { } line)
                    {
                        Append(line);
                    }
                }
                catch (Exception ended) when (ended is IOException or ObjectDisposedException)
                {
                    // The process was disposed while its stream was being read: nothing more will come.
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    public string Text()
    {
        using var reader = new StreamReader(new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Whether the process has logged that it listens on <paramref name="host"/>:<paramref name="port"/>, the line Weir writes once
    /// its listener is up. Only the process that bound the port writes it, so it tells this server from another that holds the port.
    /// </summary>
    public bool SaysListeningOn(string host, int port) =>
        Text().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.EndsWith($" listening on {host}:{port}", StringComparison.Ordinal));

    public string Tail() => Last(TailLines);

    /// <summary>The log of a server that exited before it was ready, whole unless it is very long.</summary>
    public string StartFailure() => Last(StartFailureLines);

    private string Last(int count)
    {
        var lines = Text().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? "(the log is empty)" : string.Join(Environment.NewLine, lines.TakeLast(count));
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            _disposed = true;
            _writer.Dispose();
        }
    }

    private void Append(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_writeLock)
        {
            if (!_disposed)
            {
                _writer.WriteLine(line);
            }
        }
    }
}
