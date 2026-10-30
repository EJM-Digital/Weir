using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using Weir.Host;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Tests;

namespace Weir.Api.Tests.Startup;

/// <summary>
/// The server program run in this process, as its command line and environment ask: <c>--healthcheck</c> and a start that is
/// refused. The program reads the process environment, so these run one at a time and put every variable back.
/// </summary>
[Collection(ProcessEnvironment.CollectionName)]
public sealed class EntryPointCommandTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(60);

    private readonly string _home = Path.Join(Path.GetTempPath(), "weir-entry-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _original = [];

    public void Dispose()
    {
        foreach (var (name, value) in _original)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The server may still hold a file for a moment; the temp folder is cleared with the rest.
        }
    }

    [Fact]
    public void The_word_recover_is_no_command_so_it_is_taken_for_a_start_and_a_setting_that_is_not_valid_refuses_it()
    {
        Set("WEIR_HOME", _home);

        var (exitCode, _) = Run("recover", "--port", "not-a-port");

        Assert.Equal(1, exitCode);
        Assert.True(File.Exists(Path.Join(_home, StartupNotes.ErrorFileName)), "A start that is refused leaves the note the tray reads; a command would not.");
    }

    [Fact]
    public void A_health_check_with_nothing_listening_exits_nonzero()
    {
        var (exitCode, _) = Run("--healthcheck", "--port", FreePort().ToString(CultureInfo.InvariantCulture));

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void A_start_with_a_setting_that_is_not_valid_is_refused_and_leaves_the_note_the_tray_reads()
    {
        Set("WEIR_HOME", _home);

        var (exitCode, _) = Run("--port", "not-a-port");

        Assert.Equal(1, exitCode);
        Assert.True(File.Exists(Path.Join(_home, StartupNotes.ErrorFileName)));
    }

    [Fact]
    public void A_start_on_the_port_the_environment_names_that_another_program_holds_exits_with_the_code_that_asks_to_be_tried_again()
    {
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        Set("WEIR_HOME", _home);
        Set("PORT", ((IPEndPoint)holder.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture));
        SetQuiet();

        var (exitCode, _) = Run();

        Assert.Equal(StartRefusal.PortInUseExitCode, exitCode);
        Assert.False(File.Exists(Path.Join(_home, StartupNotes.ErrorFileName)), "A port in use is not a refusal: the tray must try again.");
    }

    [Fact]
    public void A_start_that_cannot_listen_for_any_other_reason_is_not_taken_for_a_port_in_use()
    {
        Set("WEIR_HOME", _home);
        SetQuiet();

        // A documentation address (RFC 5737) that no network card holds: the bind fails, but not because a port is taken.
        var failure = Assert.Throws<TargetInvocationException>(() => Run("--host", "203.0.113.7", "--port", FreePort().ToString(CultureInfo.InvariantCulture)));

        Assert.Equal(SocketError.AddressNotAvailable, Assert.IsType<SocketException>(failure.InnerException).SocketErrorCode);
    }

    [WindowsFact("The tray's stop request is a Windows named event.")]
    [SupportedOSPlatform("windows")]
    public async Task A_server_started_by_the_program_runs_until_the_tray_asks_it_to_stop_and_then_exits_cleanly()
    {
        Set("WEIR_HOME", _home);
        SetQuiet();
        var port = FreePort().ToString(CultureInfo.InvariantCulture);
        var eventName = StopRequest.EventName(Environment.ProcessId);

        var run = Task.Run(() => Run("--host", "localhost", "--port", port));
        await WaitForTheStopEventAsync(eventName, run);
        using (var stopEvent = EventWaitHandle.OpenExisting(eventName))
        {
            stopEvent.Set();
        }

        var (exitCode, _) = await run.WaitAsync(Ceiling);
        Assert.Equal(0, exitCode);
    }

    [SupportedOSPlatform("windows")]
    private static async Task WaitForTheStopEventAsync(string eventName, Task<(int ExitCode, string Output)> run)
    {
        await Eventually.ThatAsync(
            () =>
            {
                Assert.False(run.IsCompleted, "The server ended before it could be asked to stop.");
                return EventWaitHandle.TryOpenExisting(eventName, out var existing) && Dispose(existing);
            },
            Ceiling);
    }

    private static bool Dispose(EventWaitHandle handle)
    {
        handle.Dispose();
        return true;
    }

    // The program is the compiled entry point; its output is the process's standard output.
    private static (int ExitCode, string Output) Run(params string[] args)
    {
        var entryPoint = typeof(WeirServer).Assembly.EntryPoint!;
        var original = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            var exitCode = (int)entryPoint.Invoke(null, [args])!;
            return (exitCode, captured.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    /// <summary>Keeps these starts off the network and away from the machine's own data.</summary>
    private void SetQuiet()
    {
        Set("WEIR_ARTWORK_GATEWAY_URL", "off");
        Set(WeirTestServer.ManagerWorkflowSyncVariable, "0");
        Set("WEIR_PROCESSING_WORKER_COUNT", "0");
    }

    private void Set(string name, string value)
    {
        _original.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>Tests that change the process's environment variables run alone, so none of them sees another's.</summary>
[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class ProcessEnvironment
{
    public const string CollectionName = "Process environment";
}
