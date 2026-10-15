using System.Text.Json;

namespace Weir.Contract.Tests.Harness;

/// <summary>
/// The server's own word that it is up: the <c>startup-progress.json</c> it keeps in its data folder, whose record names the process
/// that wrote it. It reaches a reader when the file is written, unlike the server's output, which the harness is handed through the
/// thread pool and which arrives late, by minutes, when the test process is busy.
/// </summary>
internal static class ServerReadyFile
{
    private const string FileName = "startup-progress.json";

    /// <summary>Whether process <paramref name="processId"/> has recorded in <paramref name="home"/> that it is ready; an earlier server's record is not its.</summary>
    public static bool SaysReady(string home, int processId)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(home, FileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var record = JsonDocument.Parse(stream);
            var root = record.RootElement;
            return root.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out var written) && written == processId
                && root.TryGetProperty("phase", out var phase) && phase.ValueKind == JsonValueKind.String && phase.GetString() == "ready";
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or JsonException)
        {
            // Not written yet, or being replaced at this moment: the next look finds it.
            return false;
        }
    }
}
