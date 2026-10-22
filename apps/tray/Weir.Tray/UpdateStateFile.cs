using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weir.Tray;

/// <summary>Where the tray is with an update, as System › About shows it.</summary>
enum UpdatePhase
{
    /// <summary>Nothing is under way. An update the last check found, and did not download, is named in <c>version</c>.</summary>
    Idle,
    Checking,
    Downloading,

    /// <summary>The update in <c>version</c> is downloaded and waits to be installed.</summary>
    Downloaded,

    /// <summary>The last check or download did not work; <c>failure</c> says why.</summary>
    Failed,
}

/// <summary>
/// update-state.json in the runtime home: what the tray is doing about updates, written as it goes. The server watches the
/// file and tells every open page, so System › About follows a check, a download and the restart that installs it without
/// asking. Only the tray writes it.
/// </summary>
static class UpdateStateFile
{
    internal const string FileName = "update-state.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter<UpdatePhase>(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Replaces the file. <c>downloaded</c> is kept beside <c>state</c> for the readers that only ask that. <c>checkedAt</c> is when
    /// the last check that reached GitHub was made, whether or not it found a newer version, so a person who asked for one is told.
    /// </summary>
    internal static void Write(string runtimeHome, UpdatePhase state, string? version, string? failure = null, DateTimeOffset? checkedAt = null) =>
        AtomicFile.WriteAllText(
            runtimeHome,
            FileName,
            JsonSerializer.Serialize(new Contents(state, state == UpdatePhase.Downloaded, version, failure, checkedAt?.UtcDateTime), Json));

    private sealed record Contents(UpdatePhase State, bool Downloaded, string? Version, string? Failure, DateTime? CheckedAt);
}
