using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Weir.Core.Processing;
using Weir.Infrastructure.IO;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Runtime;

/// <summary>A copy of Weir's data taken before an update changed it.</summary>
/// <param name="DatabasePath">The copy of the database.</param>
/// <param name="TakenAt">When the copy was taken, from its file name.</param>
/// <param name="ToVersion">The version the update was to.</param>
/// <param name="FromVersion">The version that was running, when the copy's maker knew it.</param>
public sealed record PreUpdateBackupFile(string DatabasePath, DateTimeOffset TakenAt, string ToVersion, string? FromVersion);

/// <summary>Weir could not save a copy of its data before updating, so it changed nothing. The message is for the operator.</summary>
public sealed class PreUpdateBackupException : Exception
{
    public const string Headline = "Couldn't save a copy of its data before updating";

    private const string Lead = "Weir couldn't save a copy of its data before updating, so it didn't change anything: ";

    public PreUpdateBackupException()
        : this("Something unexpected went wrong. The details are in Weir's log files.", null)
    {
    }

    public PreUpdateBackupException(string reason)
        : this(reason, null)
    {
    }

    /// <param name="reason">What stopped the copy and what to do about it, in plain words.</param>
    /// <param name="innerException">The failure behind it, for the log.</param>
    public PreUpdateBackupException(string reason, Exception? innerException)
        : base(Lead + reason, innerException)
    {
        Reason = reason;
    }

    /// <summary>What stopped the copy and what to do about it, without the lead.</summary>
    public string Reason { get; }
}

/// <summary>
/// The copy of Weir's data taken before an update changes it: a consistent copy of the database (<c>VACUUM INTO</c>, never a
/// file copy of a database with a write-ahead log), the settings files and the two secrets in the data folder, kept in
/// <c>{backup_dir}/pre-update</c> under one name: <c>weir-{old revision number}-to-{new version}-{UTC time}.db</c>, with each
/// other file beside it as <c>weir-…-{UTC time}.{file name}</c>. The secrets are part of it because the database's saved API
/// keys can only be read with the credentials secret.
/// </summary>
/// <remarks>
/// <para>
/// It is taken by the running server when the tray is about to apply an update (<see cref="TrayUpdateBackupWatcher"/>), and again by
/// the server that starts on an older database, which is the net under every other way an update arrives and sees the data as the
/// update leaves it: the first copy stays beside it as an extra. Older copies are pruned to <see cref="MaxKept"/> - 1 before a new one is written, so there are never
/// more than <see cref="MaxKept"/> on disk, and only files this class names are ever touched.
/// </para>
/// <para>
/// The copy holds sessions and saved connection secrets, so the folder and its files are owner-only: mode 700 and 600 where the
/// platform has them, and on Windows an explicit access list for the account Weir runs as, SYSTEM and Administrators (the
/// backup folder can be anywhere).
/// </para>
/// </remarks>
public sealed class PreUpdateBackup
{
    public const int MaxKept = 5;
    public const string FolderName = "pre-update";

    private const string DatabaseKind = "db";
    private const string UnfinishedKind = "db.partial";
    private const string DetailsKind = "backup.json";
    private const string TimeFormat = "yyyyMMdd'T'HHmmss'Z'";
    private const int RoomPercent = 120;

    private static readonly string[] SettingsFiles =
    [
        LanAccessFile.FileName,
        UpdateFiles.SettingsFileName,
        DirectPlayEvaluation.OverrideFileName,
    ];

    private static readonly string[] SecretFiles = ["session.secret", "credentials.secret"];

    private static readonly Regex OwnName = BuildOwnName();

    private static readonly UnixFileMode OwnerOnlyFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _backupDir;
    private readonly string _home;
    private readonly string _version;
    private readonly string? _fromVersion;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Action<string>? _saving;
    private readonly Action? _working;
    private readonly IFileSystem _files;

    /// <param name="backupDir">The backup folder (<c>WEIR_BACKUP_DIR</c>); copies go in its <see cref="FolderName"/> folder.</param>
    /// <param name="home">Weir's data folder, where the settings files and secrets are.</param>
    /// <param name="version">The version that is about to update the database.</param>
    /// <param name="time">Names the copy.</param>
    /// <param name="logger">Where the copy is announced and a failure is described.</param>
    /// <param name="fromVersion">The version running now, when it is known.</param>
    /// <param name="saving">Told the sentence that says a copy is being saved, just before the copy starts.</param>
    /// <param name="working">Called from SQLite while the copy is made and checked: the proof that a copy of a large database is still going.</param>
    /// <param name="files">The file system the copies are written to; the real one unless a test substitutes it.</param>
    public PreUpdateBackup(
        string backupDir,
        string home,
        string version,
        TimeProvider time,
        ILogger logger,
        string? fromVersion = null,
        Action<string>? saving = null,
        Action? working = null,
        IFileSystem? files = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        _backupDir = backupDir;
        _home = home;
        _version = FileNamePart(version);
        _fromVersion = string.IsNullOrWhiteSpace(fromVersion) ? null : fromVersion.Trim();
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _saving = saving;
        _working = working;
        _files = files ?? PhysicalFileSystem.Instance;
    }

    /// <summary>How much room the volume has, or null when that cannot be told. A test replaces it.</summary>
    internal Func<string, long?> FreeSpaceOf { get; set; } = FreeSpaceOnVolume;

    /// <summary>Writes the copy of the database to the path. A test replaces it.</summary>
    internal Action<SqliteConnection, string> CopyDatabase { get; set; } = VacuumInto;

    /// <summary>Where the copies are kept for <paramref name="backupDir"/>.</summary>
    public static string FolderIn(string backupDir) => Path.Join(backupDir, FolderName);

    /// <summary>The newest copy in <paramref name="backupDir"/>, or null when none has been taken.</summary>
    public static PreUpdateBackupFile? Latest(string backupDir, IFileSystem? files = null)
    {
        files ??= PhysicalFileSystem.Instance;
        var folder = FolderIn(backupDir);
        try
        {
            return Parts(files, folder)
                .Where(file => file.Part.Kind == DatabaseKind)
                .OrderByDescending(file => file.Part.TakenAt)
                .ThenByDescending(file => file.Part.Stem, StringComparer.Ordinal)
                .Select(file => Describe(files, folder, file.Part, file.Path))
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Saves the copy and returns it: older copies are pruned, the room is checked, and the database, settings and secrets are
    /// copied and the database copy is checked to open at <paramref name="fromRevision"/>. Throws
    /// <see cref="PreUpdateBackupException"/> when any step fails (whatever it was), after removing what it had written.
    /// </summary>
    public PreUpdateBackupFile Save(SqliteConnection connection, string fromRevision)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(fromRevision);
        var folder = FolderIn(_backupDir);
        var written = new List<string>();
        try
        {
            return SaveInFolder(connection, fromRevision, folder, written);
        }
#pragma warning disable CA1031 // Whatever stops the copy is a failed backup: the update must not go ahead without one.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Remove(written);
            _logger.LogError(
                exception, "Weir could not save a copy of its data before updating from={From} to={To} folder={Folder}", fromRevision, _version, folder);
            throw new PreUpdateBackupException(PreUpdateBackupFailure.Describe(exception, folder), exception);
        }
    }

    private PreUpdateBackupFile SaveInFolder(SqliteConnection connection, string fromRevision, string folder, List<string> written)
    {
        EnsureFolder(folder);
        var revision = RevisionNumber(fromRevision);
        // The oldest copy goes first, so the folder never holds more than MaxKept, even for a moment.
        Prune(folder, MaxKept - 1);
        var databaseBytes = _files.GetLength(connection.DataSource);
        var needed = databaseBytes * RoomPercent / 100;
        if (FreeSpaceOf(folder) is { } free && free < needed)
        {
            throw new NotEnoughRoomException(needed, free);
        }

        var saving = $"Saving a copy of Weir's data ({PreUpdateBackupFailure.SizeText(databaseBytes)}) before updating…";
        _logger.LogInformation("{Saving} from={From} to={To}", saving, fromRevision, _version);
        _saving?.Invoke(saving);

        var takenAt = _time.GetUtcNow();
        var stem = $"weir-{revision}-to-{_version}-{takenAt.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture)}";
        var database = Path.Join(folder, $"{stem}.{DatabaseKind}");
        var unfinished = Path.Join(folder, $"{stem}.{UnfinishedKind}");
        written.Add(unfinished);
        _files.DeleteFile(unfinished);
        CopyDatabase(connection, unfinished);
        VerifyDatabase(unfinished, fromRevision, _working);
        _files.MoveFile(unfinished, database, overwrite: false);
        written.Remove(unfinished);
        written.Add(database);
        RestrictFile(database);

        foreach (var name in SettingsFiles.Concat(SecretFiles))
        {
            var source = Path.Join(_home, name);
            if (_files.FileExists(source))
            {
                var copy = Path.Join(folder, $"{stem}.{name}");
                CreateCopy(source, copy, written);
            }
        }

        var details = Path.Join(folder, $"{stem}.{DetailsKind}");
        written.Add(details);
        _files.WriteAllBytes(details, JsonSerializer.SerializeToUtf8Bytes(new Details(_fromVersion, _version)));
        RestrictFile(details);

        _logger.LogInformation("Before updating, Weir saved a copy of its data from={From} to={To} path={Path}", fromRevision, _version, database);
        Prune(folder, MaxKept);
        return new PreUpdateBackupFile(database, takenAt, _version, _fromVersion);
    }

    private sealed record Details(string? FromVersion, string ToVersion);

    private void CreateCopy(string source, string copy, List<string> written)
    {
        if (_files.FileExists(copy))
        {
            throw new IOException($"{copy} already exists");
        }

        written.Add(copy);
        _files.CopyFile(source, copy, overwrite: false);
        RestrictFile(copy);
    }

    /// <summary>Keeps the newest <paramref name="keep"/> copies; removes the rest, and any copy a crash left unfinished.</summary>
    private void Prune(string folder, int keep)
    {
        try
        {
            var parts = Parts(_files, folder).ToList();
            var kept = parts
                .Where(file => file.Part.Kind == DatabaseKind)
                .OrderByDescending(file => file.Part.TakenAt)
                .ThenByDescending(file => file.Part.Stem, StringComparer.Ordinal)
                .Take(keep)
                .Select(file => file.Part.Stem)
                .ToHashSet(StringComparer.Ordinal);
            Remove(parts.Where(file => file.Part.Kind == UnfinishedKind || !kept.Contains(file.Part.Stem)).Select(file => file.Path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Weir could not remove older copies of its data from {Folder}", folder);
        }
    }

    private static void VacuumInto(SqliteConnection connection, string path)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $path";
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }

    /// <summary>Opens the copy on its own connection, checks it is whole and that it records <paramref name="revision"/>.</summary>
    private static void VerifyDatabase(string path, string revision, Action? working)
    {
        using var copy = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        copy.Open();
        using var reporting = working is null ? null : SqliteWorkProgress.Report(copy, working);
        using var check = copy.CreateCommand();
        check.CommandText = "PRAGMA quick_check";
        var verdict = Convert.ToString(check.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (verdict != "ok")
        {
            throw new IOException($"the copy of the database did not pass its integrity check ({verdict})");
        }

        var recorded = SchemaMigrator.ReadRecordedRevision(copy);
        if (recorded != revision)
        {
            throw new IOException($"the copy of the database records revision '{recorded}', not '{revision}'");
        }
    }

    private static long? FreeSpaceOnVolume(string folder)
    {
        try
        {
            var full = Path.GetFullPath(folder);
            var volume = OperatingSystem.IsWindows() ? Path.GetPathRoot(full) : full;
            return volume is null ? null : new DriveInfo(volume).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A volume that will not say how much room it has (a network share) is not a reason to refuse.
            return null;
        }
    }

    private void EnsureFolder(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            _files.CreateDirectory(folder);
            OwnerOnlyAccess.RestrictFolder(folder);
            return;
        }

        _files.CreateDirectory(folder, OwnerOnlyFolder);
        _files.SetUnixFileMode(folder, OwnerOnlyFolder);
    }

    private void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            OwnerOnlyAccess.RestrictFile(path);
        }
        else
        {
            _files.SetUnixFileMode(path, OwnerOnlyFile);
        }
    }

    private void Remove(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                _files.DeleteFile(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be removed now is found again by the next prune.
            }
        }
    }

    /// <summary>The leading number of a revision: <c>0076</c> for <c>0076_handoff_skipped_extras</c>.</summary>
    private static string RevisionNumber(string revision)
    {
        var end = revision.IndexOf('_', StringComparison.Ordinal);
        return end < 0 ? revision : revision[..end];
    }

    /// <summary>
    /// <paramref name="value"/> made safe for a file name. A version's build metadata (after <c>+</c>) is left out, as version
    /// comparisons leave it out.
    /// </summary>
    private static string FileNamePart(string value)
    {
        var plus = value.IndexOf('+', StringComparison.Ordinal);
        var core = (plus < 0 ? value : value[..plus]).Trim();
        return string.Concat(core.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
    }

    /// <summary>One of this class's own files: the copy it belongs to, when it was taken, and what it is.</summary>
    private sealed record Part(string Stem, string Revision, string Version, DateTimeOffset TakenAt, string Kind);

    private static List<(string Path, Part Part)> Parts(IFileSystem files, string folder)
    {
        if (!files.DirectoryExists(folder))
        {
            return [];
        }

        return files.ListFiles(folder, new EnumerationOptions { IgnoreInaccessible = false })
            .Select(path => (Path: path, Part: Classify(System.IO.Path.GetFileName(path))))
            .Where(file => file.Part is not null)
            .Select(file => (file.Path, file.Part!))
            .ToList();
    }

    /// <summary>
    /// What <paramref name="fileName"/> is, or null when it is not exactly a name this class writes:
    /// <c>weir-NNNN-to-{version}-{time}.{kind}</c>. Anything else in the folder is somebody else's.
    /// </summary>
    private static Part? Classify(string fileName)
    {
        var match = OwnName.Match(fileName);
        if (!match.Success
            || !DateTimeOffset.TryParseExact(
                match.Groups["at"].Value, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var takenAt))
        {
            return null;
        }

        return new Part(match.Groups["stem"].Value, match.Groups["rev"].Value, match.Groups["version"].Value, takenAt, match.Groups["kind"].Value);
    }

    private static PreUpdateBackupFile Describe(IFileSystem files, string folder, Part part, string databasePath)
    {
        string? from = null;
        var to = part.Version;
        try
        {
            using var details = files.OpenFile(Path.Join(folder, $"{part.Stem}.{DetailsKind}"), FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = JsonDocument.Parse(details);
            from = Text(document.RootElement, "FromVersion");
            to = Text(document.RootElement, "ToVersion") ?? to;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A copy without its details file is still a copy; its name says the version it was to.
        }

        return new PreUpdateBackupFile(databasePath, part.TakenAt, to, from);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Regex BuildOwnName()
    {
        var kinds = new[] { UnfinishedKind, DatabaseKind, DetailsKind }.Concat(SettingsFiles).Concat(SecretFiles).Select(Regex.Escape);
        return new Regex(
            @"^(?<stem>weir-(?<rev>\d{4})-to-(?<version>[0-9A-Za-z][0-9A-Za-z.\-]*?)-(?<at>\d{8}T\d{6}Z))\.(?<kind>" + string.Join('|', kinds) + ")$",
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(1));
    }
}
