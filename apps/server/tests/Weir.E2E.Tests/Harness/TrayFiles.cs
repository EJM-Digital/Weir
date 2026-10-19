using System.Globalization;
using System.Text.Json;
using Weir.Contract.Tests.Harness;

namespace Weir.E2E.Tests.Harness;

/// <summary>
/// The Windows tray's side of its hand-off with the server. The tray is a separate program the browser tests do not start. It
/// tells the server where it is with an update by writing a small file in Weir's data folder, and it hears a button pressed in
/// System › About through a flag file the server leaves there. So the tests play the tray with those files: they take the flag
/// and write the state, whole and renamed into place as the tray does it. Nothing in the database is touched.
/// </summary>
public static class TrayFiles
{
    public const string CheckRequest = "update-check-now";
    public const string DownloadRequest = "update-download-now";
    public const string ApplyRequest = "update-apply-now";

    private const string UpdateState = "update-state.json";
    private const string Heartbeat = "tray-heartbeat.json";

    /// <summary>The tray says it is alive, <paramref name="ago"/> ago: the server offers the update buttons only while that is recent.</summary>
    public static void WriteHeartbeat(string home, TimeSpan? ago = null) =>
        File.WriteAllText(Path.Join(home, Heartbeat), JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow - (ago ?? TimeSpan.Zero) }));

    /// <summary>The update choices the tray follows: whether Weir looks for updates at all. A new install has no such file and checks for nothing.</summary>
    public static void WriteUpdateSettings(string home, bool checkForUpdates) =>
        File.WriteAllText(Path.Join(home, "update-settings.json"), JsonSerializer.Serialize(new { mode = "Auto", checkForUpdates }));

    public static void WriteUpdateState(string home, bool downloaded, string version) =>
        Replace(home, string.Create(CultureInfo.InvariantCulture, $"{{\"downloaded\": {(downloaded ? "true" : "false")}, \"version\": \"{version}\"}}"));

    /// <summary>The tray is at <paramref name="step"/> (idle, checking, downloading, downloaded or failed) with an update to <paramref name="version"/>, if it has found one.</summary>
    public static void WriteUpdateStep(string home, string step, string? version = null, string? failure = null) =>
        Replace(home, JsonSerializer.Serialize(new { state = step, downloaded = step == "downloaded", version, failure }));

    /// <summary>The tray has installed the update: the file that said one was waiting is gone.</summary>
    public static void ClearUpdateState(string home) => File.Delete(Path.Join(home, UpdateState));

    /// <summary>Waits for a button in System › About to ask the tray for <paramref name="request"/>, and takes the flag as the tray does.</summary>
    public static async Task TakeRequestAsync(string home, string request)
    {
        var flag = Path.Join(home, request);
        await Poll.UntilAsync(() => Task.FromResult(File.Exists(flag)), $"System › About to ask the tray for {request}");
        File.Delete(flag);
    }

    private static void Replace(string home, string text)
    {
        var scratch = Path.Join(home, $".{UpdateState}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(scratch, text);
        File.Move(scratch, Path.Join(home, UpdateState), overwrite: true);
    }
}
