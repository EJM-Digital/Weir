using System.Diagnostics;

namespace Weir.Contract.Tests.Harness;

/// <summary>Everything one server process wrote to its output and error streams, kept in a file next to its data.</summary>
public sealed class ServerLog : IDisposable
{
    private const int TailLines = 40;

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

    /// <summary>Copies the process's output and error lines into the log.</summary>
    public void Follow(Process process)
    {
        process.OutputDataReceived += (_, line) => Append(line.Data);
        process.ErrorDataReceived += (_, line) => Append(line.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    /// <summary>Adds a line of the harness's own, such as why the server was started again.</summary>
    public void Note(string text) => Append(text);

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

    public string Tail()
    {
        var lines = Text().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? "(the log is empty)" : string.Join(Environment.NewLine, lines.TakeLast(TailLines));
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
