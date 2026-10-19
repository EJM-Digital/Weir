using System.Text.Json;

namespace Weir.Tray;

/// <summary>
/// tray-heartbeat.json in the runtime home: when the tray last said it is alive. The server offers System › About's update
/// buttons only while it is fresh, because a flag left for a tray that is not there is never answered. The server does not
/// publish a change when it is written, so it can be written as often as it needs to be. A tray run from a copy that was not
/// installed with the installer says so (<c>installed: false</c>), because it answers no request and the person is told why.
/// </summary>
static class TrayHeartbeatFile
{
    internal const string FileName = "tray-heartbeat.json";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static void Write(string runtimeHome, DateTimeOffset at, bool installed) =>
        AtomicFile.WriteAllText(runtimeHome, FileName, JsonSerializer.Serialize(new { At = at, Installed = installed }, Json));
}
