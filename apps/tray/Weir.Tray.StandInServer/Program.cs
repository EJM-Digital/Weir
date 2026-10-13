// A stand-in for WeirServer.exe in the tray's process tests. Like the server, it creates its stop event
// (Weir.Tray.ServerStopRequest), says "ready" on its standard output, and exits with code 0 when the event is set.
// "ignore-stop-requests" leaves the event unhonoured and "no-stop-event" never creates it; either way it stays alive
// until it is killed. It is a program of our own on the Windows subsystem, not a system console program such as
// ping: a console program can be handed a console window when its parent is killed, and where Windows Terminal is the
// default terminal that opens a window on the desktop (#806, #821).
//
// Started with the "--port N" the tray gives the server, it also answers GET /ready on that port at 127.0.0.1: 503 until
// WEIR_STANDIN_READY_AFTER_MS milliseconds have passed (200 at once if unset), or never listening at all when that is -1.
// WEIR_STANDIN_STOP_AFTER_MS is how long it takes to exit once asked to stop. WEIR_STANDIN_ERROR_LINE, when set, is written to its
// standard error after "ready".
using System.Net;
using System.Net.Sockets;
using System.Text;
using Weir.Tray;

const string IgnoreStopRequests = "ignore-stop-requests";
const string NoStopEvent = "no-stop-event";
const string PortArgument = "--port";
const string ReadyAfterVariable = "WEIR_STANDIN_READY_AFTER_MS";
const string StopAfterVariable = "WEIR_STANDIN_STOP_AFTER_MS";
const string ErrorLineVariable = "WEIR_STANDIN_ERROR_LINE";
const string ReadyLine = "ready";
const int StoppedExitCode = 0;
const int LifetimeEndedExitCode = 1;

// A server that cannot start: when fail-start.txt sits beside it, it counts the start in starts.txt, writes that file's text to
// startup-error.txt in WEIR_HOME as the real server does when it refuses to start, and exits at once with code 1.
var failStart = Path.Combine(AppContext.BaseDirectory, "fail-start.txt");
if (File.Exists(failStart))
{
    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "starts.txt"), "start\n");
    File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("WEIR_HOME")!, "startup-error.txt"), File.ReadAllText(failStart));
    return LifetimeEndedExitCode;
}

// Bounds how long a process orphaned by a killed test run can linger.
var lifetime = TimeSpan.FromMinutes(2);

using var stopEvent = args.Contains(NoStopEvent)
    ? null
    : new EventWaitHandle(initialState: false, EventResetMode.ManualReset, ServerStopRequest.EventName(Environment.ProcessId));
Console.Out.WriteLine(ReadyLine);
Console.Out.Flush();
if (Environment.GetEnvironmentVariable(ErrorLineVariable) is { Length: > 0 } errorLine)
{
    Console.Error.WriteLine(errorLine);
    Console.Error.Flush();
}

var portIndex = Array.IndexOf(args, PortArgument);
if (portIndex >= 0 && portIndex + 1 < args.Length && int.TryParse(args[portIndex + 1], out var port) && ReadyAfter() is { } readyAfter)
{
    _ = Task.Run(() => ServeReadiness(port, readyAfter));
}

if (stopEvent is null || args.Contains(IgnoreStopRequests))
{
    await Task.Delay(lifetime);
    return LifetimeEndedExitCode;
}

if (!stopEvent.WaitOne(lifetime))
{
    return LifetimeEndedExitCode;
}

await Task.Delay(TimeSpan.FromMilliseconds(Milliseconds(StopAfterVariable) ?? 0));
return StoppedExitCode;

static long? Milliseconds(string variable) =>
    long.TryParse(Environment.GetEnvironmentVariable(variable), out var value) ? value : null;

// Null when the stand-in is not to listen at all.
static TimeSpan? ReadyAfter() => Milliseconds(ReadyAfterVariable) switch
{
    < 0 => null,
    { } milliseconds => TimeSpan.FromMilliseconds(milliseconds),
    _ => TimeSpan.Zero,
};

static async Task ServeReadiness(int port, TimeSpan readyAfter)
{
    var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
    var started = DateTime.UtcNow;
    while (true)
    {
        using var client = await listener.AcceptTcpClientAsync();
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer);
            var ready = DateTime.UtcNow - started >= readyAfter;
            var status = ready ? "200 OK" : "503 Service Unavailable";
            var body = ready ? "{\"ready\":true}" : "{\"ready\":false}";
            var response = $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
        }
        catch (IOException)
        {
            // The asker gave up before the answer arrived.
        }
    }
}
