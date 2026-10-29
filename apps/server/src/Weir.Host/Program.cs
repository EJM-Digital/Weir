using Weir.Core;
using Weir.Core.Configuration;
using Weir.Host;
using Weir.Infrastructure.Logging;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Sqlite;

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
    await ServerRun.RunAsync(
        app,
        failure =>
        {
            if (PortInUse.Caused(failure))
            {
                startupLog.LogCritical("Weir cannot listen: port {Port} is already in use by another program", listenPort);
            }
        }).ConfigureAwait(false);
}
catch (Exception exception) when (PortInUse.Caused(exception))
{
    return StartRefusal.ReportPortInUse(listenPort, NonBlockingConsole.ForStandardError());
}

return 0;
