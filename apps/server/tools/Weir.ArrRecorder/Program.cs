using System.Text;
using Weir.ArrRecorder;

// Records a real Sonarr or Radarr: sets the app up through its own API, then drives Weir's own manager client code against it and keeps
// every request and reply as a recording. The app is started by compose.yaml in this folder; see README.md for the environment.
//
// Exit codes: 0 every step recorded, 1 a step failed, 2 the recorder was not set up.
Console.OutputEncoding = new UTF8Encoding(false);
RecorderSettings settings;
try
{
    settings = RecorderSettings.FromEnvironment();
}
catch (InvalidOperationException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    return 2;
}

try
{
    using var scenario = new ArrScenario(settings);
    return await scenario.RunAsync();
}
catch (Exception exception)
{
    await Console.Error.WriteLineAsync($"The session stopped: {exception}");
    return 1;
}
