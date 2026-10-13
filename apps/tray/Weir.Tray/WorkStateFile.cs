using System.Text.Json;

namespace Weir.Tray;

/// <summary>
/// The server's answer to "is Weir idle?", in work-state.json in the runtime home. The server writes it every few
/// seconds while an update waits to install (#875). It is a file, not a request, because the tray has no signed-in
/// session and the server should have no route that answers without one; the runtime home is readable only by the
/// account running Weir.
/// </summary>
static class WorkStateFile
{
    internal const string FileName = "work-state.json";

    /// <summary>
    /// How old an answer may be and still describe the server now. The server refreshes it every 15 seconds, so an
    /// older one means the server is stopped, restarting or stuck, and none of those is idle.
    /// </summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// What the server said, judged at <paramref name="now"/>. Anything short of a fresh, readable answer is
    /// <see cref="ServerWork.Unknown"/>, never <see cref="ServerWork.Idle"/>: a missing answer must not let an install
    /// through. The server replaces the file every few seconds by renaming a new one over it, so a refused open or a missing name is
    /// looked at again first; a file that stays refused is <see cref="ServerWork.NotRead"/>, which says nothing and so does not
    /// start the wait for quiet over.
    /// </summary>
    internal static ServerWork Read(string runtimeHome, DateTimeOffset now)
    {
        var reading = ReplacedFile.ReadText(Path.Combine(runtimeHome, FileName));
        switch (reading.State)
        {
            case ReplacedFileState.Absent:
                return ServerWork.Unknown;
            case ReplacedFileState.Busy:
                return ServerWork.NotRead;
        }

        try
        {
            var answer = JsonSerializer.Deserialize<Answer>(reading.Text, Json);
            if (answer is not { Busy: { } busy, CheckedAt: { } checkedAt } || (now - checkedAt).Duration() > MaxAge)
            {
                return ServerWork.Unknown;
            }
            return busy ? ServerWork.Busy : ServerWork.Idle;
        }
        catch (JsonException)
        {
            return ServerWork.Unknown;
        }
    }

    private sealed record Answer(bool? Busy, DateTimeOffset? CheckedAt);
}
