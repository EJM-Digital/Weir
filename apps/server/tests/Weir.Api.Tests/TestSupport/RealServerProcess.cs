using System.Diagnostics;
using System.Globalization;

namespace Weir.Api.Tests;

/// <summary>
/// The real server (<c>Weir.dll</c> from this test's output) as a child process on a fresh data folder, with its output read as it goes.
/// A test waits on its events (exit, or /ready answering) with a ceiling that only a broken server reaches.
/// </summary>
internal sealed class RealServerProcess : IDisposable
{
    private readonly Process _process;
    private readonly Task<string> _output;
    private readonly Task<string> _error;
    private bool _disposed;

    private RealServerProcess(Process process, string host, int port)
    {
        _process = process;
        Host = host;
        Port = port;
        _output = process.StandardOutput.ReadToEndAsync();
        _error = process.StandardError.ReadToEndAsync();
    }

    internal string Host { get; }

    internal int Port { get; }

    /// <summary>Starts the server listening on <paramref name="host"/> and <paramref name="port"/>, keeping its data in <paramref name="home"/>.</summary>
    internal static RealServerProcess Start(string home, string host, int port)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Weir.dll"));
        start.ArgumentList.Add("--host");
        start.ArgumentList.Add(host);
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        start.Environment["WEIR_HOME"] = home;
        start.Environment["WEIR_ENV"] = "production";
        start.Environment["WEIR_SESSION_SECRET"] = new string('s', 48);
        start.Environment["WEIR_CREDENTIALS_SECRET"] = new string('c', 48);
        start.Environment["WEIR_ARTWORK_GATEWAY_URL"] = "off";
        start.Environment["WEIR_MEDIA_MANAGER_WORKFLOW_SYNC_ENABLED"] = "0";
        var process = Process.Start(start) ?? throw new InvalidOperationException("The server could not be started.");
        return new RealServerProcess(process, host, port);
    }

    /// <summary>Where the dump and report of this server are, when it missed a ceiling; empty otherwise.</summary>
    internal string Diagnostics { get; private set; } = string.Empty;

    internal int ExitCode => _process.ExitCode;

    /// <summary>Everything the server has written to standard error, once it has exited.</summary>
    internal Task<string> ErrorAsync() => _error;

    /// <summary>What the server wrote to standard output and error, once it has exited; for a failure message.</summary>
    internal async Task<string> EverythingAsync() => await _output + await _error;

    /// <summary>Waits for the server to exit, for no longer than <paramref name="ceiling"/>; false when it is still running.</summary>
    internal async Task<bool> ExitsAsync(TimeSpan ceiling)
    {
        using var cancelled = new CancellationTokenSource(ceiling);
        try
        {
            await _process.WaitForExitAsync(cancelled.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            Diagnostics = ChildDiagnostics.Capture(_process, nameof(ExitsAsync));
            return false;
        }
    }

    /// <summary>Waits until /ready answers 200 or the server exits, for no longer than <paramref name="ceiling"/>.</summary>
    internal async Task<bool> BecomesReadyAsync(TimeSpan ceiling)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var cancelled = new CancellationTokenSource(ceiling);
        var address = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}/ready"));
        while (!cancelled.IsCancellationRequested && !_process.HasExited)
        {
            try
            {
                using var response = await http.GetAsync(address, cancelled.Token);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // Not listening yet.
            }

            await Task.Delay(100, CancellationToken.None);
        }

        if (!_process.HasExited)
        {
            Diagnostics = ChildDiagnostics.Capture(_process, nameof(BecomesReadyAsync));
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var kept = ChildServerStop.StopOrKill(_process, nameof(RealServerProcess));
        if (kept.Length > 0)
        {
            Diagnostics = kept;
        }

        _process.Dispose();
    }
}
