using Weir.Core;
using Weir.Core.Configuration;
using Weir.Host;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Logging;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Sqlite;

if (args.Length > 0 && string.Equals(args[0], "recover", StringComparison.Ordinal))
{
    // Local console access only: no HTTP route runs this. Reaching the server's own shell (and so
    // its database under WEIR_HOME) is the recovery factor — see RecoverCommand and #454/#553.
    try
    {
        return await RecoverCommand.RunAsync(
            args[1..],
            WeirServer.CurrentRuntime(),
            Console.Out,
            Console.Error,
            new ConsoleRecoverPasswordPrompt()).ConfigureAwait(false);
    }
    catch (WeirConfigurationException exception)
    {
        return StartRefusal.Report(exception, WeirServer.CurrentRuntime(), NonBlockingConsole.ForStandardError(), leaveNote: false);
    }
}

if (args.Contains("--version"))
{
    Console.WriteLine(WeirVersion.Resolve(Environment.GetEnvironmentVariable("WEIR_VERSION")));
    return 0;
}

if (args.Contains("--healthcheck"))
{
    return await HealthCheckCommand.RunAsync(args, WeirServer.CurrentRuntime(), Console.Error).ConfigureAwait(false);
}

WebApplication app;
try
{
    app = WeirServer.Build(args, WeirServer.CurrentRuntime(), builder =>
    {
        // The Windows tray stops the server through this event (#833). Elsewhere the platform's own signals do it.
        if (OperatingSystem.IsWindows())
        {
            builder.Services.AddStopRequestListener(StopRequest.EventName(Environment.ProcessId));
        }
    });
}
catch (Exception exception) when (exception is WeirConfigurationException or DatabaseSchemaMismatchException or PreUpdateBackupException)
{
    // Operator-facing refusals: print the message, not a stack trace.
    return StartRefusal.Report(exception, WeirServer.CurrentRuntime(), NonBlockingConsole.ForStandardError());
}

var listenPort = app.Services.GetRequiredService<ServerListenOptions>().Port;
var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Weir.Host.Startup");
try
{
    await app.RunAsync().ConfigureAwait(false);
}
catch (Exception exception) when (PortInUse.Caused(exception))
{
    startupLog.LogCritical("Weir cannot listen: port {Port} is already in use by another program", listenPort);
    return StartRefusal.ReportPortInUse(listenPort, NonBlockingConsole.ForStandardError());
}

return 0;
