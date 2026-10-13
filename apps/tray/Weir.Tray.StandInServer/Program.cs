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
//
// What the supervision tests steer it with, all by environment: WEIR_STANDIN_EXIT_CODE makes it exit at once with that code
// (a server that crashes as it starts) until the attempt number the tray passes reaches WEIR_STANDIN_SUCCEED_AT_ATTEMPT;
// WEIR_STANDIN_NO_STOP_EVENT never creates the stop event, as a server hung before it could;
// WEIR_STANDIN_SAY_FILE repeats each line appended to that file onto standard output;
// WEIR_STANDIN_PHASE (with WEIR_STANDIN_EXPECT_S) writes one startup-progress.json record for itself at start, and a test writes
// the later ones; WEIR_STANDIN_READY_FILE makes /ready answer 200 only once that file exists. When it answers 200 for the first time it
// also reports the phase "ready" for itself on standard output (when the tray asked for progress lines there), as the real server does
// once its listener is up, and the tray takes /ready for this server's only after that. Every start is also noted in
// standin-starts.txt in WEIR_HOME with the attempt number and the failures the tray passed (WEIR_START_ATTEMPT, WEIR_START_FAILURES).
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
const string ExitCodeVariable = "WEIR_STANDIN_EXIT_CODE";
const string SucceedAtVariable = "WEIR_STANDIN_SUCCEED_AT_ATTEMPT";
const string NoStopEventVariable = "WEIR_STANDIN_NO_STOP_EVENT";
const string PhaseVariable = "WEIR_STANDIN_PHASE";
const string ExpectVariable = "WEIR_STANDIN_EXPECT_S";
const string ReadyFileVariable = "WEIR_STANDIN_READY_FILE";
const string SayFileVariable = "WEIR_STANDIN_SAY_FILE";
const string ReadyLine = "ready";
const int StoppedExitCode = 0;
const int LifetimeEndedExitCode = 1;

NoteStart();

// A server that cannot start: when fail-start.txt sits beside it, it counts the start in starts.txt, writes that file's text to
// startup-error.txt in WEIR_HOME as the real server does when it refuses to start, and exits at once with code 1.
var failStart = Path.Combine(AppContext.BaseDirectory, "fail-start.txt");
if (File.Exists(failStart))
{
    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "starts.txt"), "start\n");
    File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("WEIR_HOME")!, "startup-error.txt"), File.ReadAllText(failStart));
    return LifetimeEndedExitCode;
}

// Exits at once while the attempt is earlier than WEIR_STANDIN_SUCCEED_AT_ATTEMPT (every attempt when that is not set).
var attemptNumber = int.TryParse(Environment.GetEnvironmentVariable("WEIR_START_ATTEMPT"), out var attemptSaid) ? attemptSaid : 1;
var succeedAt = int.TryParse(Environment.GetEnvironmentVariable(SucceedAtVariable), out var succeedAtSaid) ? succeedAtSaid : int.MaxValue;
if (int.TryParse(Environment.GetEnvironmentVariable(ExitCodeVariable), out var exitNow) && attemptNumber < succeedAt)
{
    return exitNow;
}

if (Environment.GetEnvironmentVariable(PhaseVariable) is { Length: > 0 } phase)
{
    WriteProgress(phase, long.TryParse(Environment.GetEnvironmentVariable(ExpectVariable), out var expect) ? expect : 0);
}

// Bounds how long a process orphaned by a killed test run can linger.
var lifetime = TimeSpan.FromMinutes(2);

using var stopEvent = args.Contains(NoStopEvent) || Environment.GetEnvironmentVariable(NoStopEventVariable) is { Length: > 0 }
    ? null
    : new EventWaitHandle(initialState: false, EventResetMode.ManualReset, ServerStopRequest.EventName(Environment.ProcessId));
Console.Out.WriteLine(ReadyLine);
Console.Out.Flush();
if (Environment.GetEnvironmentVariable(ErrorLineVariable) is { Length: > 0 } errorLine)
{
    Console.Error.WriteLine(errorLine);
    Console.Error.Flush();
}

if (Environment.GetEnvironmentVariable(SayFileVariable) is { Length: > 0 } sayFile)
{
    _ = Task.Run(() => RepeatToStandardOutput(sayFile));
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
    var reportedReady = false;
    while (true)
    {
        using var client = await listener.AcceptTcpClientAsync();
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer);
            var readyFile = Environment.GetEnvironmentVariable(ReadyFileVariable);
            var ready = readyFile is { Length: > 0 } ? File.Exists(readyFile) : DateTime.UtcNow - started >= readyAfter;
            if (ready && !reportedReady)
            {
                reportedReady = true;
                ReportReadyOnStandardOutput();
            }

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

// Prints each line a test appends to the file onto standard output, as the real server prints its progress lines, so a test can
// make the stand-in report through the pipe the tray owns.
static async Task RepeatToStandardOutput(string path)
{
    long printed = 0;
    while (true)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(printed, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync() is { } line)
            {
                Console.Out.WriteLine(line);
                Console.Out.Flush();
            }

            printed = stream.Position;
        }
        catch (IOException)
        {
            // Not there yet, or being written; looked at again.
        }

        await Task.Delay(10);
    }
}

static void ReportReadyOnStandardOutput()
{
    if (Environment.GetEnvironmentVariable("WEIR_PROGRESS_STDOUT") is not { Length: > 0 })
    {
        return;
    }

    var at = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    Console.Out.WriteLine($"weir-start-progress {{\"pid\":{Environment.ProcessId},\"phase\":\"ready\",\"step\":0,\"steps\":0,\"expect_within_s\":0,\"at\":\"{at}\"}}");
    Console.Out.Flush();
}

static void NoteStart()
{
    var home = Environment.GetEnvironmentVariable("WEIR_HOME");
    if (home is { Length: > 0 })
    {
        var line = $"pid={Environment.ProcessId};attempt={Environment.GetEnvironmentVariable("WEIR_START_ATTEMPT")};pipe={Environment.GetEnvironmentVariable("WEIR_PROGRESS_STDOUT")};failures={Environment.GetEnvironmentVariable("WEIR_START_FAILURES")}{Environment.NewLine}";
        File.AppendAllText(Path.Combine(home, "standin-starts.txt"), line);
    }
}

// One progress record, as the real server writes it (StartupProgress): whole, then renamed into place.
static void WriteProgress(string phase, long expectWithinSeconds)
{
    var home = Environment.GetEnvironmentVariable("WEIR_HOME")!;
    var at = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    var json = $"{{\"pid\":{Environment.ProcessId},\"phase\":\"{phase}\",\"step\":0,\"steps\":0,\"expect_within_s\":{expectWithinSeconds},\"at\":\"{at}\"}}";
    var scratch = Path.Combine(home, $".startup-progress.{Guid.NewGuid():n}.tmp");
    File.WriteAllText(scratch, json);
    File.Move(scratch, Path.Combine(home, "startup-progress.json"), overwrite: true);
}
