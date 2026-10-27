using System.Globalization;
using Microsoft.Data.Sqlite;
using Weir.Contract.Tests.Harness;

namespace Weir.E2E.Tests.Harness;

/// <summary>
/// Plain-SQL resets and seeds against the running server's SQLite file, the way an operator's second process
/// would write to it. The server does not cache the rows touched here.
/// </summary>
public static class E2EDatabase
{
    private const int BusyTimeoutSeconds = 30;

    /// <summary>
    /// Resets all per-test state so each test starts at the setup page. Users, sessions and suite settings are
    /// cleared. The migration-seeded Processing libraries have their folders cleared rather than being deleted,
    /// because they are the only path store and a scope with no library at all has nowhere to resolve to.
    /// </summary>
    public static void ResetPerTestState(string databasePath)
    {
        using var connection = Open(databasePath);
        using var transaction = connection.BeginTransaction();
        SeedSql.Execute(connection, "UPDATE libraries SET watched_folder = '', work_folder = '', output_folder = ''");
        SeedSql.Execute(connection, "DELETE FROM user_sessions");
        SeedSql.Execute(connection, "DELETE FROM users");
        SeedSql.Execute(connection, "DELETE FROM suite_settings");
        SeedSql.Execute(
            connection,
            "UPDATE operator_settings SET work_temp_stale_sweep_enabled = 1, work_temp_stale_sweep_interval_seconds = NULL, " +
            "unclaimed_handback_cleanup_enabled = 0, unclaimed_handback_window_days = 14, unclaimed_handback_cleanup_interval_seconds = NULL, " +
            "keep_failed_work_files = 0, runner_budget_enabled = 0 WHERE id = 1");
        transaction.Commit();
    }

    /// <summary>Gives the account this role (admin, operator or viewer), as an administrator elsewhere would: Weir has one account, so no screen makes the others.</summary>
    public static void SetRole(string databasePath, string username, string role)
    {
        using var connection = Open(databasePath);
        using var transaction = connection.BeginTransaction();
        SeedSql.Execute(connection, "UPDATE users SET role = $role WHERE username = $username", ("$role", role), ("$username", username));
        transaction.Commit();
    }

    /// <summary>Adds an account with a role, as an operator's second process would; the server reads its users afresh on every sign-in.</summary>
    public static void AddUser(string databasePath, string username, string passwordHash, string role)
    {
        using var connection = Open(databasePath);
        using var transaction = connection.BeginTransaction();
        SeedSql.InsertUser(connection, username, passwordHash, role);
        transaction.Commit();
    }

    /// <summary>
    /// Points a workflow at a watched folder without Weir's checks, as a media manager's own sync can leave one: the folders then overlap another
    /// workflow's, which Weir refuses the next time that workflow is saved.
    /// </summary>
    public static void SetWatchedFolder(string databasePath, long libraryId, string watchedFolder)
    {
        using var connection = Open(databasePath);
        using var transaction = connection.BeginTransaction();
        SeedSql.Execute(connection, "UPDATE libraries SET watched_folder = $folder WHERE id = $id", ("$folder", watchedFolder), ("$id", libraryId));
        transaction.Commit();
    }

    /// <summary>
    /// A read-only viewer of this name, added while the server runs and no administrator exists yet: no screen makes one, so a test of
    /// a name that is taken seeds the row, as an operator's second process would.
    /// </summary>
    public static void InsertViewerNamed(string databasePath, string username)
    {
        AddUser(databasePath, username, SeededAccounts.ViewerPasswordHash, "viewer");
    }

    /// <summary>The read-only viewer <see cref="SeededAccounts.ViewerUsername"/>, added while the server runs and no administrator exists yet.</summary>
    public static void InsertViewer(string databasePath) => InsertViewerNamed(databasePath, SeededAccounts.ViewerUsername);

    /// <summary>Ends every signed-in session while keeping the accounts, as an expiry does: the next request from a browser is unauthenticated.</summary>
    public static void EndAllSessions(string databasePath)
    {
        using var connection = Open(databasePath);
        using var transaction = connection.BeginTransaction();
        SeedSql.Execute(connection, "DELETE FROM user_sessions");
        transaction.Commit();
    }

    /// <summary>
    /// Moves the end of a timed pause a minute into the past, as the passing of that time would: the pause is still on until the server's
    /// own sweep lifts it, which is what a test of "the pause runs out by itself" waits for.
    /// </summary>
    public static void RunTimedPauseOut(string databasePath)
    {
        var ended = DateTime.UtcNow.AddMinutes(-1).ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        using var connection = Open(databasePath);
        using var transaction = connection.BeginTransaction();
        SeedSql.Execute(
            connection,
            "UPDATE suite_settings SET processing_paused_until = $ended WHERE id = 1 AND processing_paused = 1 AND processing_paused_until IS NOT NULL",
            ("$ended", ended));
        transaction.Commit();
    }

    /// <summary>One <c>activity_events</c> row, stamped now in the stored shape (<c>YYYY-MM-DD HH:MM:SS.ffffff</c>, UTC).</summary>
    public static void InsertActivityEvent(string databasePath, string eventType, string module, string title, string? detail = null)
    {
        var createdAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        using var connection = Open(databasePath);
        using var transaction = connection.BeginTransaction();
        SeedSql.Execute(
            connection,
            "INSERT INTO activity_events (created_at, event_type, module, title, detail) VALUES ($created, $type, $module, $title, $detail)",
            ("$created", createdAt),
            ("$type", eventType),
            ("$module", module),
            ("$title", title),
            ("$detail", string.IsNullOrEmpty(detail) ? null : detail));
        transaction.Commit();
    }

    /// <summary>
    /// A file of the workflow that failed to process, as the remove dialog finds one: the row a failed pass leaves, with no
    /// fingerprint recorded. Returns its id. The file itself must be in the workflow's watched folder, and Weir registers a file it finds there
    /// in its own time, so the row may already be there: it is taken over rather than refused as a duplicate. A test that needs the server to leave
    /// the row alone pauses processing first.
    /// </summary>
    public static long InsertFailedFile(string databasePath, long libraryId, string relativePath, long sizeBytes)
    {
        using var connection = Open(databasePath);
        using var transaction = connection.BeginTransaction();
        var id = Convert.ToInt64(
            SeedSql.Scalar(
                connection,
                "INSERT INTO files (library_id, relative_path, status, status_reason, size_bytes, last_seen_at) " +
                "VALUES ($library, $path, 'processing_failed', 'Weir could not read the audio track.', $size, CURRENT_TIMESTAMP) " +
                "ON CONFLICT (library_id, relative_path) DO UPDATE SET status = excluded.status, status_reason = excluded.status_reason, " +
                "size_bytes = excluded.size_bytes, last_seen_at = excluded.last_seen_at RETURNING id",
                ("$library", libraryId),
                ("$path", relativePath),
                ("$size", sizeBytes)),
            CultureInfo.InvariantCulture);
        transaction.Commit();
        return id;
    }

    /// <summary>
    /// A job in <paramref name="status"/>, as the row a worker leaves: for a state the browser cannot reach by pressing buttons, such as a job that did its media
    /// work and could not record it. Returns its id.
    /// </summary>
    public static long InsertJob(string databasePath, string dedupeKey, string jobKind, string status, string? lastError = null)
    {
        var at = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        using var connection = Open(databasePath);
        using var transaction = connection.BeginTransaction();
        var id = Convert.ToInt64(
            SeedSql.Scalar(
                connection,
                "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status, attempt_count, max_attempts, last_error, created_at, updated_at) " +
                "VALUES ($key, $kind, NULL, $status, 1, 3, $error, $at, $at) RETURNING id",
                ("$key", dedupeKey),
                ("$kind", jobKind),
                ("$status", status),
                ("$error", lastError),
                ("$at", at)),
            CultureInfo.InvariantCulture);
        transaction.Commit();
        return id;
    }

    /// <summary>Moves a job to <paramref name="status"/> as another process of Weir's would, without the page being told.</summary>
    public static void SetJobStatus(string databasePath, long jobId, string status)
    {
        using var connection = Open(databasePath);
        using var transaction = connection.BeginTransaction();
        SeedSql.Execute(connection, "UPDATE jobs SET status = $status WHERE id = $id", ("$status", status), ("$id", jobId));
        transaction.Commit();
    }

    /// <summary>How many finished hand-offs Weir still owes a report: the reports it kept because the manager did not answer.</summary>
    public static long OwedReportCount(string databasePath)
    {
        using var connection = Open(databasePath);
        return Convert.ToInt64(
            SeedSql.Scalar(connection, "SELECT COUNT(*) FROM media_manager_handoffs WHERE pending_report_json IS NOT NULL"),
            CultureInfo.InvariantCulture);
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = BusyTimeoutSeconds,
        }.ToString());
        connection.Open();
        SeedSql.Execute(connection, $"PRAGMA busy_timeout = {BusyTimeoutSeconds * 1000}");
        return connection;
    }
}
