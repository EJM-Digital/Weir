namespace Weir.Infrastructure.Activity;

/// <summary>
/// The names a <c>data.changed</c> stream frame can carry, one for each kind of data a screen shows. A component that changes
/// that data publishes its topic on <see cref="DataChangePublisher"/>; the web app maps each topic to the queries it refreshes.
/// </summary>
public static class DataTopics
{
    /// <summary>Processing paused, resumed, or a timed pause ran out.</summary>
    public const string Pause = "pause";

    /// <summary>Whether Weir is ready: its workers, tools and folders.</summary>
    public const string Readiness = "readiness";

    /// <summary>How many files are worked on at once.</summary>
    public const string FilesAtOnce = "files_at_once";

    /// <summary>The maintenance window and what runs in it.</summary>
    public const string Maintenance = "maintenance";

    /// <summary>The workflows (libraries) and their settings.</summary>
    public const string Libraries = "libraries";

    /// <summary>A workflow's library scan was queued, indexed more files or ended, or its scheduled scan and clean changed.</summary>
    public const string LibraryScan = "library_scan";

    /// <summary>A workflow's folder check gave a different answer: its own folders, or what its media managers and download clients say about them.</summary>
    public const string FolderChecks = "folder_checks";

    /// <summary>The state of an update: available, downloading, ready.</summary>
    public const string Update = "update";

    /// <summary>Who can reach Weir over the network.</summary>
    public const string NetworkAccess = "network_access";

    /// <summary>The media manager and download client connections.</summary>
    public const string Connections = "connections";

    /// <summary>Weir's own settings.</summary>
    public const string Settings = "settings";

    /// <summary>Which devices are chosen for the Direct Play badge.</summary>
    public const string DirectPlayDevices = "direct_play_devices";

    /// <summary>Configuration backups.</summary>
    public const string Backups = "backups";

    /// <summary>Files kept without processing them again.</summary>
    public const string KeptFiles = "kept_files";

    /// <summary>Request and runtime figures.</summary>
    public const string Metrics = "metrics";

    /// <summary>The job queue.</summary>
    public const string Jobs = "jobs";
}
