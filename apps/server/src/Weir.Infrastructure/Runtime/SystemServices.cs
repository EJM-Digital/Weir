using System.Globalization;
using System.Text;
using System.Text.Json;
using Weir.Core.Configuration;
using Weir.Core.Json;
using Weir.Core.Updates;

namespace Weir.Infrastructure.Runtime;

/// <summary>
/// The tray's update files under <c>WEIR_HOME</c>: settings, the state the tray writes, the flags that ask it to check, download
/// or apply now, and the work state.
/// </summary>
public sealed class UpdateFiles
{
    public const string SettingsFileName = "update-settings.json";
    public const string StateFileName = "update-state.json";
    public const string ApplyFlagFileName = "update-apply-now";
    public const string CheckFlagFileName = "update-check-now";
    public const string DownloadFlagFileName = "update-download-now";

    /// <summary>What the tray rewrites every 30 s to say it is alive.</summary>
    public const string TrayHeartbeatFileName = "tray-heartbeat.json";

    /// <summary>How old the tray's heartbeat may be and still count: three beats.</summary>
    public static readonly TimeSpan TrayHeartbeatMaxAge = TimeSpan.FromSeconds(90);

    /// <summary>How old a check or download flag may be and still read as under way: the tray takes one up within a second.</summary>
    public static readonly TimeSpan RequestMaxAge = TimeSpan.FromMinutes(2);

    /// <summary>The tray's record of an update it held back for want of a copy of Weir's data: <c>{ "version": …, "reason": … }</c>.</summary>
    public const string NotAppliedFileName = "update-not-applied.json";

    /// <summary>What the tray reads to learn whether Weir is idle; written only while an update waits to install (#875).</summary>
    public const string WorkStateFileName = "work-state.json";

    private readonly WeirOptions _options;
    private readonly TimeProvider _time;

    // The last good reading of each file the tray replaces under us. A file that is refused for a moment (the tray is renaming a new
    // one over it) says nothing, so the last good reading stands in until the next look; only a file that is not there, or that is
    // damaged, changes it. See ReplacedFile.
    private readonly Lock _lastGood = new();
    private DateTimeOffset? _lastBeat;
    private string? _lastStateText;
    private string? _lastNotAppliedReason;

    public UpdateFiles(WeirOptions options, TimeProvider? time = null)
    {
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Whether there is a tray to answer a request: this is the Windows install, and the tray has said it is alive lately. A
    /// Docker or source install has no tray, and a flag left for one that has quit or died is never taken up.
    /// </summary>
    public bool TrayIsRunning()
    {
        if (DetectInstallType(_options.RuntimeKind) != "windows")
        {
            return false;
        }

        // A heartbeat that cannot be read just now is the last one seen, ageing as time passes: a tray that is being refused for a
        // moment is not a tray that has quit.
        var (state, text) = Look(TrayHeartbeatFileName);
        DateTimeOffset? beat;
        if (state == ReplacedFileState.Busy)
        {
            lock (_lastGood)
            {
                beat = _lastBeat;
            }
        }
        else
        {
            beat = text is null ? null : ParseHeartbeat(text);
            lock (_lastGood)
            {
                _lastBeat = beat;
            }
        }

        return beat is { } when && _time.GetUtcNow() - when <= TrayHeartbeatMaxAge;
    }

    private static DateTimeOffset? ParseHeartbeat(string text)
    {
        try
        {
            return WireJsonParser.Parse(text) is WireObject beat
                && beat.Get("at") is WireString at
                && DateTimeOffset.TryParse(at.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                    ? when
                    : null;
        }
        catch (WireJsonDecodeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Looks at one of the tray's files, looking again when it is refused or missing (<see cref="ReplacedFile"/>). The text is null
    /// when the file is not there, could not be read, or holds bytes that are not UTF-8.
    /// </summary>
    private (ReplacedFileState State, string? Text) Look(string fileName)
    {
        try
        {
            var reading = ReplacedFile.ReadText(Path.Join(_options.WeirHome, fileName), new UTF8Encoding(false, throwOnInvalidBytes: true));
            return (reading.State, reading.State == ReplacedFileState.Read ? reading.Text : null);
        }
        catch (DecoderFallbackException)
        {
            return (ReplacedFileState.Read, null);
        }
    }

    /// <summary>Read the update settings, with <paramref name="warn"/> called when the file is unreadable.</summary>
    public WireObject ReadSettings(Action<string>? warn = null)
    {
        var path = Path.Join(_options.WeirHome, SettingsFileName);

        // Both the Settings page and the tray replace this file by renaming a new one over it, so a refusal or a missing name is looked at
        // again before the file is called absent (the shipped default) or unreadable (notify only).
        ReplacedFileText reading;
        try
        {
            reading = ReplacedFile.ReadText(path, new UTF8Encoding(false, throwOnInvalidBytes: true));
        }
        catch (DecoderFallbackException)
        {
            warn?.Invoke(path);
            return UpdateStatus.UnreadableUpdateSettings;
        }

        if (reading.State == ReplacedFileState.Absent)
        {
            return UpdateStatus.DefaultUpdateSettings;
        }

        if (reading.State == ReplacedFileState.Busy)
        {
            warn?.Invoke(path);
            return UpdateStatus.UnreadableUpdateSettings;
        }

        var text = reading.Text;

        if (text.StartsWith('﻿'))
        {
            warn?.Invoke(path);
            return UpdateStatus.UnreadableUpdateSettings;
        }

        var parsed = UpdateStatus.ParseUpdateSettings(text);
        if (parsed is null)
        {
            warn?.Invoke(path);
            return UpdateStatus.UnreadableUpdateSettings;
        }

        return parsed;
    }

    /// <summary>Save the update settings: written whole to a unique scratch file, then renamed into place.</summary>
    public WireObject WriteSettings(string mode, bool checkOnStartup, long checkIntervalMinutes)
    {
        var text = UpdateStatus.SerializeUpdateSettings(mode, checkOnStartup, checkIntervalMinutes);
        if (OperatingSystem.IsWindows())
        {
            // The file is written as Windows text, with CRLF newlines.
            text = text.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        ReplaceFile(SettingsFileName, Encoding.UTF8.GetBytes(text));
        return UpdateStatus.UpdateSettingsOut(mode, checkOnStartup, checkIntervalMinutes);
    }

    /// <summary>
    /// Read the update state the tray writes; a missing or unreadable file reads as the default state. A check or download that
    /// was asked for and not yet taken up by the tray reads as under way, so the person sees their click at once, and
    /// <c>tray_running</c> says whether there is a tray to answer. While the tray holds an update back because it could not save
    /// a copy of Weir's data first (<see cref="NotAppliedFileName"/>), the state also says why.
    /// </summary>
    public WireObject ReadState()
    {
        var alive = TrayIsRunning();
        var state = StateAsShown(ReadStateFile(), alive).Set("tray_running", alive);
        return ReadNotAppliedReason() is { } reason ? state.Set("not_updated_reason", reason) : state;
    }

    // A step the tray says it is at, when no tray is there, is a step nobody is taking: it reads as idle. A request the tray
    // has not taken up reads as under way only while there is a tray and the flag is recent.
    private WireObject StateAsShown(WireObject written, bool trayIsRunning)
    {
        var name = (written.Get("state") as WireString)?.Value;
        if (name is UpdateStatus.StateChecking or UpdateStatus.StateDownloading)
        {
            return trayIsRunning ? written : UpdateStatus.WithStep(written, UpdateStatus.StateIdle);
        }

        if (!trayIsRunning || name is not (UpdateStatus.StateIdle or UpdateStatus.StateFailed))
        {
            return written;
        }

        if (RequestIsFresh(CheckFlagFileName))
        {
            return UpdateStatus.WithStep(written, UpdateStatus.StateChecking);
        }

        return RequestIsFresh(DownloadFlagFileName) ? UpdateStatus.WithStep(written, UpdateStatus.StateDownloading) : written;
    }

    private bool RequestIsFresh(string flagFileName)
    {
        var path = Path.Join(_options.WeirHome, flagFileName);
        return File.Exists(path) && _time.GetUtcNow() - File.GetLastWriteTimeUtc(path) <= RequestMaxAge;
    }

    private WireObject ReadStateFile()
    {
        // The tray replaces this file at every step of an update. While it is refused the last good text stands in, so a downloaded
        // update does not vanish from the page, and Apply is not refused, for as long as the tray holds the file.
        var (state, text) = Look(StateFileName);
        lock (_lastGood)
        {
            if (state == ReplacedFileState.Busy)
            {
                text = _lastStateText;
            }
            else
            {
                _lastStateText = text;
            }
        }

        return UpdateStatus.ParseUpdateState(text);
    }

    private string? ReadNotAppliedReason()
    {
        var reading = ReplacedFile.ReadText(Path.Join(_options.WeirHome, NotAppliedFileName));
        lock (_lastGood)
        {
            if (reading.State == ReplacedFileState.Busy)
            {
                return _lastNotAppliedReason;
            }

            _lastNotAppliedReason = reading.State == ReplacedFileState.Read ? ReasonIn(reading.Text) : null;
            return _lastNotAppliedReason;
        }
    }

    private static string? ReasonIn(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("reason", out var reason)
                && reason.ValueKind == JsonValueKind.String
                && reason.GetString() is { Length: > 0 } text
                    ? text
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Ask the tray to restart and install the downloaded update.</summary>
    public void WriteApplyFlag() => WriteFlag(ApplyFlagFileName);

    /// <summary>Ask the tray to look for an update now.</summary>
    public void WriteCheckFlag() => WriteFlag(CheckFlagFileName);

    /// <summary>Ask the tray to download the available update now.</summary>
    public void WriteDownloadFlag() => WriteFlag(DownloadFlagFileName);

    /// <summary>Create a flag file, or refresh its modified time when it already exists.</summary>
    private void WriteFlag(string fileName)
    {
        var path = Path.Join(_options.WeirHome, fileName);
        if (File.Exists(path))
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return;
        }

        using (File.Create(path))
        {
        }
    }

    /// <summary>Whether the tray has downloaded an update that is waiting to be installed.</summary>
    public bool HasDownloadedUpdate() => ReadState().Get("downloaded")?.IsTruthy ?? false;

    /// <summary>Tell the tray whether file work is running or could start now, as of <paramref name="checkedAt"/>.</summary>
    public void WriteWorkState(bool busy, DateTimeOffset checkedAt) =>
        ReplaceFile(WorkStateFileName, Encoding.UTF8.GetBytes(UpdateStatus.SerializeWorkState(busy, checkedAt)));

    private void ReplaceFile(string fileName, byte[] contents) => AtomicFileWriter.Replace(_options.WeirHome, fileName, contents);

    /// <summary>The install type: <c>WEIR_RUNTIME</c>, a Docker marker, a packaged Windows build, else source.</summary>
    public static string DetectInstallType(string? runtimeVariable)
    {
        var runtime = (runtimeVariable ?? string.Empty).Trim().ToLowerInvariant();
        if (runtime is "windows" or "docker" or "source")
        {
            return runtime;
        }

        if (File.Exists("/.dockerenv") || Directory.Exists("/.dockerenv"))
        {
            return "docker";
        }

        // Only the packaged Windows build is published as a single file.
        var singleFile = string.IsNullOrEmpty(typeof(UpdateFiles).Assembly.Location);
        return singleFile && OperatingSystem.IsWindows() ? "windows" : "source";
    }
}
