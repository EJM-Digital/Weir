using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Weir.Contract.Tests.Harness;

/// <summary>
/// One real Weir server process with its own data folder and its own port, so any number of them run side by
/// side. Started from the built server, never in-process. Stopping it takes down only the process this class
/// started (and what that process started). The data folder survives a restart; a restart picks a new port, so
/// create clients after it. A port is chosen by asking the system for a free one and releasing it again, so another
/// process can take it before the server binds; a server that exits for that reason alone is started again on a new
/// port, up to <see cref="MaxStartAttempts"/> times in all. A server counts as ready only when the process started here
/// has said it listens on its port (in its output, or in the progress record it keeps in its data folder) and the port
/// answers, because the process that took the port may be a server of another test, which answers on it until its own test ends.
/// </summary>
public sealed class WeirServer : IAsyncDisposable
{
    public const string KeepDataVariable = "WEIR_CONTRACT_KEEP_DATA";
    public const int MaxStartAttempts = 3;

    private const string ListenHost = "127.0.0.1";

    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ReadyProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly HttpClient ReadyProbe = new() { Timeout = ReadyProbeTimeout };

    private readonly ServerBinary _binary;
    private readonly Dictionary<string, string> _environment;
    private readonly Func<int> _pickPort;
    private readonly List<ServerLog> _logs = [];
    private readonly string? _evidenceName = FailureEvidence.CurrentName;
    private Process? _process;
    private ServerLog? _current;
    private int? _keptPort;

    private WeirServer(ServerBinary binary, string home, Dictionary<string, string> environment, Func<int> pickPort)
    {
        _binary = binary;
        Home = home;
        _environment = environment;
        _pickPort = pickPort;
    }

    public string Home { get; }

    /// <summary>The name the logs of this server are kept under (<see cref="FailureEvidence"/>); null when it was started outside a test.</summary>
    internal string? EvidenceName => _evidenceName;

    public Uri BaseUrl { get; private set; } = new($"http://{ListenHost}/");

    public string DatabasePath => Path.Combine(Home, "data", "weir.sqlite3");

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>The id of the server's process, or 0 when it is stopped.</summary>
    internal int ProcessId => _process is { } process ? process.Id : 0;

    /// <summary>Starts a server with a fresh data folder and waits until it reports ready.</summary>
    public static Task<WeirServer> StartNewAsync(IReadOnlyDictionary<string, string>? environment = null) =>
        StartWithPortsAsync(environment, FreePort);

    /// <summary>Starts a server whose ports come from <paramref name="pickPort"/>, so a test can make one clash.</summary>
    internal static async Task<WeirServer> StartWithPortsAsync(IReadOnlyDictionary<string, string>? environment, Func<int> pickPort)
    {
        var binary = ServerBinary.Locate();
        ServerLedger.Shared.StopOrphans();
        var home = Directory.CreateTempSubdirectory("weir_contract_").FullName;
        var settings = new Dictionary<string, string>(environment ?? new Dictionary<string, string>());
        var server = new WeirServer(binary, home, settings, pickPort);
        try
        {
            await server.LaunchAsync();
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }

        return server;
    }

    public WeirClient CreateClient(IReadOnlyDictionary<string, string>? defaultHeaders = null) => new(BaseUrl, defaultHeaders);

    /// <summary>A client signed in as the admin, created through bootstrap the first time.</summary>
    public async Task<WeirClient> CreateAdminClientAsync(IReadOnlyDictionary<string, string>? defaultHeaders = null)
    {
        var client = CreateClient(defaultHeaders);
        try
        {
            await client.EnsureAdminAsync();
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return client;
    }

    /// <summary>
    /// Stops the server and starts it again on the same data. It comes back on a new port unless <paramref name="samePort"/> is
    /// set, which a browser page that stays open across the restart needs: its address is the one it reconnects to.
    /// </summary>
    public async Task RestartAsync(IReadOnlyDictionary<string, string>? environmentChanges = null, bool samePort = false)
    {
        await StopAsync();
        foreach (var (name, value) in environmentChanges ?? new Dictionary<string, string>())
        {
            _environment[name] = value;
        }

        _keptPort = samePort ? BaseUrl.Port : null;
        try
        {
            await LaunchAsync();
        }
        finally
        {
            _keptPort = null;
        }
    }

    /// <summary>Stops the server and what it started. The data folder stays.</summary>
    public async Task StopAsync()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        var processId = process.Id;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
            if (_current is not null)
            {
                await OutputInLogAsync(_current);
            }
        }
        finally
        {
            ServerLedger.Shared.Forget(processId);
            process.Dispose();
        }
    }

    /// <summary>Stops the server, opens its SQLite file for seeding or inspection, and restarts it when disposed (or leaves it stopped when <paramref name="restart"/> is false).</summary>
    public async Task<StoppedDatabase> StopForDatabaseAsync(bool restart = true)
    {
        var wasRunning = IsRunning;
        await StopAsync();
        return await StoppedDatabase.OpenAsync(DatabasePath, restart && wasRunning ? LaunchAsync : () => Task.CompletedTask);
    }

    public string LogText() => string.Join(Environment.NewLine, _logs.Select(log => log.Text()));

    /// <summary>
    /// Waits until a line of the log satisfies <paramref name="wanted"/>. A line reaches the log by way of the server's output queue, its
    /// pipe and the harness's reader, so a request the server has answered can be answered before its log line is here: a test that
    /// expects a line waits for it.
    /// </summary>
    public async Task WaitForLogLineAsync(Func<string, bool> wanted, string what) =>
        await Poll.UntilAsync(() => Task.FromResult(LogText().Split(Environment.NewLine).Any(wanted)), what);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_evidenceName is not null)
        {
            // Kept for the test (or test class) that used this server; the framework deletes it again unless that test failed.
            FailureEvidence.Archive(_evidenceName, Home);
        }

        foreach (var log in _logs)
        {
            log.Dispose();
        }

        if (Environment.GetEnvironmentVariable(KeepDataVariable) != "1")
        {
            await DataFolderCleanup.DeleteAsync(Home, DatabasePath);
        }
    }

    private async Task LaunchAsync()
    {
        using var startPlace = await ServerStartGate.EnterAsync();
        string? lostPortNote = null;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await StartOnNewPortAsync(lostPortNote);
                return;
            }
            catch (PortTakenException taken) when (attempt < MaxStartAttempts)
            {
                lostPortNote =
                    $"contract harness: port {taken.Port} was taken before the server could listen on it; starting again on a new port (attempt {attempt + 1} of {MaxStartAttempts}).";
                await StopAsync();
            }
        }
    }

    private async Task StartOnNewPortAsync(string? note)
    {
        var port = _keptPort ?? _pickPort();
        BaseUrl = new Uri($"http://{ListenHost}:{port}/");
        var log = new ServerLog(Path.Combine(Home, "contract-logs", $"server-{_logs.Count + 1}.log"));
        _logs.Add(log);
        _current = log;
        if (note is not null)
        {
            log.Note(note);
        }

        var (program, leadingArguments) = _binary.Command();
        var start = new ProcessStartInfo(program)
        {
            WorkingDirectory = _binary.WorkingFolder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var listenArguments = new[] { "--host", ListenHost, "--port", port.ToString(CultureInfo.InvariantCulture) };
        foreach (var argument in leadingArguments.Concat(listenArguments))
        {
            start.ArgumentList.Add(argument);
        }

        ServerEnvironment.Apply(start.Environment, Home, BaseUrl, _environment);

        _process = Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start.");
        log.Follow(_process);
        ServerLedger.Shared.Record(_process);
        await WaitUntilReadyAsync(_process, log, port);
    }

    private async Task WaitUntilReadyAsync(Process process, ServerLog log, int port)
    {
        var progress = new StartProgressWatch(process.Id, TimeProvider.System);
        var lastProblem = "no answer yet";
        while (true)
        {
            if (process.HasExited)
            {
                // Waits for the last of its output to reach the log, which is what says why it exited.
                await process.WaitForExitAsync();
                await OutputInLogAsync(log);
                var message =
                    $"The Weir server exited with code {process.ExitCode} before it was ready.{Environment.NewLine}Last lines of its log ({log.Path}):{Environment.NewLine}{log.StartFailure()}";
                throw ServerBindFailure.IsPortInUse(log.Text())
                    ? new PortTakenException(port, message)
                    : new InvalidOperationException(message);
            }

            var problem = await ProbeReadyAsync();
            if (problem is null && !log.SaysListeningOn(ListenHost, port) && !ServerReadyFile.SaysReady(Home, process.Id))
            {
                // Whatever answers at this address is not known to be this process. The port was picked and released before the
                // server started, so another server may have taken it in between and answer for itself while this one is about to
                // fail to bind. Only this process saying that it listens there makes the answer its own: in its output, or in the
                // record of its own that it keeps in its data folder, which does not wait for the output to be delivered.
                problem = "something answers, but this server has not said it is listening there";
            }

            if (problem is null)
            {
                return;
            }

            lastProblem = problem;
            progress.Observe(ReadProgress());
            if (progress.IsHung(out var silentFor))
            {
                throw new TimeoutException(
                    $"The Weir server did not report ready at {BaseUrl}ready: it has said nothing about its start for {silentFor.TotalSeconds:0}s (last phase {progress.Phase ?? "none"}; {lastProblem}).{Environment.NewLine}Last lines of its log ({log.Path}):{Environment.NewLine}{log.Tail()}");
            }

            await Task.Delay(ReadyPollInterval);
        }
    }

    /// <summary>
    /// Waits for what the server printed before it ended to reach its log. A child of the server that outlives it can hold the
    /// streams open, so the wait has an end; what has arrived by then is what there is.
    /// </summary>
    private static async Task OutputInLogAsync(ServerLog log)
    {
        try
        {
            await log.OutputEnded.WaitAsync(OutputGrace);
        }
        catch (TimeoutException)
        {
            // A grandchild kept a pipe open.
        }
    }

    /// <summary>The server's progress record as it stands, or null when there is none yet or the file is busy.</summary>
    private string? ReadProgress()
    {
        try
        {
            using var stream = new FileStream(Path.Combine(Home, "startup-progress.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception unavailable) when (unavailable is IOException or UnauthorizedAccessException)
        {
            // Not written yet, or being replaced at this moment: the next look finds it.
            return null;
        }
    }

    /// <summary>Null when the server is ready, otherwise what it answered instead.</summary>
    private async Task<string?> ProbeReadyAsync()
    {
        try
        {
            using var response = await ReadyProbe.GetAsync(new Uri(BaseUrl, "ready"));
            var body = await response.Content.ReadAsStringAsync();
            var ready = response.StatusCode == HttpStatusCode.OK && (bool?)JsonNode.Parse(body)?["ready"] == true;
            return ready ? null : $"HTTP {(int)response.StatusCode} {body}";
        }
        catch (HttpRequestException problem)
        {
            return problem.Message;
        }
        catch (TaskCanceledException)
        {
            return "no answer within the probe timeout";
        }
    }

    internal static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class PortTakenException(int port, string message) : InvalidOperationException(message)
    {
        public int Port { get; } = port;
    }
}
