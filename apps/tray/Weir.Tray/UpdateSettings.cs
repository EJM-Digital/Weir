using System.Text.Json;

namespace Weir.Tray;

/// <summary>The update choices in update-settings.json, which the tray and the server's Settings page both write.</summary>
sealed class UpdateSettings
{
    internal const string FileName = "update-settings.json";
    internal const int DefaultCheckIntervalMinutes = 60;

    public UpdateMode Mode { get; set; } = UpdateMode.Auto;
    public bool CheckOnStartup { get; set; } = true;
    public int CheckIntervalMinutes { get; set; } = DefaultCheckIntervalMinutes;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static UpdateSettings Load(string runtimeHome)
    {
        var path = Path.Combine(runtimeHome, FileName);

        // The server's Settings page and the tray both replace this file by renaming a new one over it, so a refusal or a missing name is
        // looked at again before it is called a fresh install or a file that cannot be read.
        var reading = ReplacedFile.ReadText(path);

        // No file is a fresh install: nobody has chosen yet, so the shipped default applies.
        if (reading.State == ReplacedFileState.Absent)
        {
            return new UpdateSettings();
        }
        if (reading.State == ReplacedFileState.Busy)
        {
            // Not damaged, only not readable now, after several looks. This read decides whether to install, so it falls back to the mode
            // that installs nothing; the next read, after the file is back, decides again.
            TrayLog.Write($"{FileName} could not be read just now (it was being replaced or scanned). Using NotifyOnly for now.");
            return new UpdateSettings { Mode = UpdateMode.NotifyOnly };
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
            return new UpdateSettings { Mode = UpdateMode.NotifyOnly };
        }
    }

    // The server writes this same file when the Settings page saves; AtomicFile keeps the two writers from
    // renaming each other's scratch file into place, and keeps Load's damaged-file fallback unreachable in practice.
    internal void Save(string runtimeHome) =>
        AtomicFile.WriteAllText(runtimeHome, FileName, JsonSerializer.Serialize(this, JsonOptions));
}
