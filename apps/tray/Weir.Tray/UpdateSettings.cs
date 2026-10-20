using System.Text.Json;

namespace Weir.Tray;

/// <summary>The update choices in update-settings.json, which the tray and the server's Settings page both write.</summary>
sealed class UpdateSettings
{
    internal const string FileName = "update-settings.json";
    internal const int DefaultCheckIntervalMinutes = 60;
    private const string DatabasePathVariable = "WEIR_DB_PATH";
    private const string DatabaseFileName = "weir.sqlite3";

    public UpdateMode Mode { get; set; } = UpdateMode.Auto;
    public bool CheckOnStartup { get; set; } = true;
    public int CheckIntervalMinutes { get; set; } = DefaultCheckIntervalMinutes;

    /// <summary>
    /// Whether the tray looks for updates at all: no check, no download, whatever the mode. A file saved before this choice
    /// existed was saved by an install that was checking, so a file without it reads as on; an install with no file is
    /// settled by <see cref="SettleFirstRun"/>.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Writes the file on the first start of a tray that finds none, so an upgrade keeps what the install was doing and a new
    /// install starts with checking off. A database already in the runtime home means Weir has run here before, and every
    /// Weir that ran here before this choice existed was checking; a runtime home with none is a new install.
    /// </summary>
    internal static void SettleFirstRun(string runtimeHome, Func<string, string?>? environment = null)
    {
        if (File.Exists(Path.Combine(runtimeHome, FileName)))
        {
            return;
        }

        var existing = WeirHasRunHere(runtimeHome, environment ?? Environment.GetEnvironmentVariable);
        try
        {
            new UpdateSettings { CheckForUpdates = existing }.Save(runtimeHome);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the file the tray reads the new-install default, which checks nothing; the next start tries again.
            TrayLog.Write($"Could not save {FileName}: {ex.Message}");
            return;
        }
        TrayLog.Write(existing
            ? "Weir has run here before, so update checking stays on."
            : "This is a new install, so update checking starts off. Setup asks whether to turn it on.");
    }

    private static bool WeirHasRunHere(string runtimeHome, Func<string, string?> environment)
    {
        var configured = environment(DatabasePathVariable)?.Trim();
        var database = string.IsNullOrEmpty(configured)
            ? Path.Combine(runtimeHome, "data", DatabaseFileName)
            : Path.GetFullPath(configured, runtimeHome);
        return File.Exists(database);
    }

    internal static UpdateSettings Load(string runtimeHome)
    {
        var path = Path.Combine(runtimeHome, FileName);

        // The server's Settings page and the tray both replace this file by renaming a new one over it, so a refusal or a missing name is
        // looked at again before it is called a fresh install or a file that cannot be read.
        var reading = ReplacedFile.ReadText(path);

        // No file is a new install, or a tray that has not started yet: nobody has chosen, so nothing is checked.
        if (reading.State == ReplacedFileState.Absent)
        {
            return new UpdateSettings { CheckForUpdates = false };
        }
        if (reading.State == ReplacedFileState.Busy)
        {
            // Not damaged, only not readable now, after several looks. This read decides whether to install, so it falls back to the mode
            // that installs nothing and checks nothing; the next read, after the file is back, decides again.
            TrayLog.Write($"{FileName} could not be read just now (it was being replaced or scanned). Using NotifyOnly for now.");
            return new UpdateSettings { Mode = UpdateMode.NotifyOnly, CheckForUpdates = false };
        }
        try
        {
            return JsonSerializer.Deserialize<UpdateSettings>(reading.Text, JsonOptions)
                ?? throw new InvalidDataException($"{FileName} parsed as null.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            // A file that exists but cannot be read means a choice was made and we cannot tell what it was. Auto
            // is the only mode that installs an update with nobody watching, so it is the one guess that can act
            // against an explicit choice: an operator who picked NotifyOnly would be auto-updated by a truncated
            // file. This read decides whether to install, so it falls back to the mode that installs nothing.
            TrayLog.Write(
                $"{FileName} could not be read ({ex.Message}). Using NotifyOnly so a damaged "
                + "file cannot install an update the operator did not choose.");
            return new UpdateSettings { Mode = UpdateMode.NotifyOnly, CheckForUpdates = false };
        }
    }

    // The server writes this same file when the Settings page saves; AtomicFile keeps the two writers from
    // renaming each other's scratch file into place, and keeps Load's damaged-file fallback unreachable in practice.
    internal void Save(string runtimeHome) =>
        AtomicFile.WriteAllText(runtimeHome, FileName, JsonSerializer.Serialize(this, JsonOptions));
}
