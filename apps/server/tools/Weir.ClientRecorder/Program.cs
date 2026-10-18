using System.Text;
using Weir.ClientRecorder;

// Records a real download client: sets it up through its own API, then drives Weir's own client code (the five IDownloadClientPort
// dialects) against it and keeps every request and reply as a recording. The client is started by compose.yaml in this folder (and
// Transmission by the Sonarr and Radarr recorder's); see README.md for the environment.
//
// Exit codes: 0 every step recorded, 1 a step failed, 2 the recorder was not set up.
Console.OutputEncoding = new UTF8Encoding(false);
ClientSettings settings;
try
{
    settings = ClientSettings.FromEnvironment();
}
catch (InvalidOperationException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    return 2;
}

try
{
    using var session = ClientSession.For(settings);
    return await session.RunAsync();
}
catch (Exception exception)
{
    await Console.Error.WriteLineAsync($"The session stopped: {exception}");
    return 1;
}
