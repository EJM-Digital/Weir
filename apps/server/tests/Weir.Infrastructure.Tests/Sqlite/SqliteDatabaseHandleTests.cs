using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using SQLitePCL;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Sqlite;

/// <summary>
/// What <see cref="SqliteDatabase"/> does with the native handles it opens, rescues and refuses: which handle comes back,
/// which is closed and what a closed one no longer does, each against a real SQLite file.
/// </summary>
public sealed class SqliteDatabaseHandleTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string DatabaseFile => _temp.Join("weir.sqlite3");

    private static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void RefuseWhen(SqliteConnection connection, Func<int, string, bool> refused) =>
        raw.sqlite3_set_authorizer(
            connection.Handle,
            (object _, int action, string param0, string _, string _, string _) => refused(action, param0) ? raw.SQLITE_DENY : raw.SQLITE_OK,
            null);

    [Fact]
    public async Task A_caller_cancelled_in_the_instant_the_busy_timeout_passes_is_cancelled_not_refused_as_busy()
    {
        var time = new FakeTimeProvider();
        var database = new SqliteDatabase(DatabaseFile, busyTimeoutMilliseconds: 2000, timeProvider: time);
        using var caller = new CancellationTokenSource();
        await database.AcquireWriteGateAsync(CancellationToken.None);
        try
        {
            var waiting = database.AcquireWriteGateAsync(caller.Token);

            // Registered after the gate's own registration, so it runs first when the caller is cancelled: by the time the
            // wait is woken, both the caller and the busy timeout have fired.
            caller.Token.Register(() => time.Advance(TimeSpan.FromMilliseconds(2000)));
            await caller.CancelAsync();

            var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting).WaitAsync(Limit);
            Assert.IsNotType<DatabaseBusyException>(cancelled);
        }
        finally
        {
            database.ReleaseWriteGate();
            database.ClearPool();
        }
    }

    [Fact]
    public async Task A_connection_left_inside_a_transaction_is_rolled_back_and_handed_out_again_not_replaced()
    {
        var database = new SqliteDatabase(DatabaseFile);
        try
        {
            var left = await database.OpenAsync();
            Run(left, "CREATE TEMP TABLE marker (value INTEGER)");
            Run(left, "BEGIN IMMEDIATE");
            await left.DisposeAsync();

            // A temp table lives and dies with its native handle: it is still there only if the rescued handle is the one handed out.
            var next = await database.OpenAsync();
            await using (next.ConfigureAwait(false))
            {
                Assert.Equal(1, raw.sqlite3_get_autocommit(next.Handle));
                using var count = next.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM temp.marker";
                Assert.Equal(0L, count.ExecuteScalar());
            }
        }
        finally
        {
            database.ClearPool();
        }
    }

    [Fact]
    public async Task A_connection_whose_rollback_is_refused_is_closed_so_it_no_longer_holds_the_write_lock()
    {
        var database = new SqliteDatabase(DatabaseFile);
        try
        {
            var left = await database.OpenAsync();
            Run(left, "CREATE TABLE notes (text TEXT)");
            Run(left, "BEGIN IMMEDIATE");
            RefuseWhen(left, (action, param0) => action == raw.SQLITE_TRANSACTION && param0 == "ROLLBACK");
            await left.DisposeAsync();

            await using var next = await database.OpenAsync();
            await using var outsider = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabaseFile, Pooling = false }.ToString());
            await outsider.OpenAsync();
            await using var noWait = outsider.CreateCommand();
            noWait.CommandText = "PRAGMA busy_timeout=0";
            await noWait.ExecuteNonQueryAsync();
            await using var begin = outsider.CreateCommand();
            begin.CommandTimeout = 1;
            begin.CommandText = "BEGIN IMMEDIATE; ROLLBACK;";

            var exception = await Record.ExceptionAsync(() => begin.ExecuteNonQueryAsync());

            Assert.Null(exception);
        }
        finally
        {
            database.ClearPool();
        }
    }

    [Fact]
    public async Task A_handle_that_is_closed_no_longer_calls_the_rollback_hook_for_the_transaction_it_ends()
    {
        var database = new SqliteDatabase(DatabaseFile, pooling: false);
        var rolledBack = 0;
        var connection = await database.OpenAsync();
        Run(connection, "BEGIN IMMEDIATE");
        raw.sqlite3_rollback_hook(connection.Handle, _ => rolledBack++, null);

        database.CloseHandle(connection);

        Assert.Equal(0, rolledBack);
    }

    [Fact]
    public async Task A_health_probe_of_a_database_that_opens_but_cannot_answer_a_query_says_it_is_not_connected()
    {
        var database = new SqliteDatabase(DatabaseFile);
        try
        {
            var idle = await database.OpenAsync();
            RefuseWhen(idle, (action, _) => action == raw.SQLITE_SELECT);
            await idle.DisposeAsync();

            Assert.False(await database.IsConnectedAsync());
        }
        finally
        {
            database.ClearPool();
        }
    }

    [Fact]
    public async Task A_health_probe_of_an_ordinary_database_says_it_is_connected()
    {
        var database = new SqliteDatabase(DatabaseFile);
        try
        {
            Assert.True(await database.IsConnectedAsync());
        }
        finally
        {
            database.ClearPool();
        }
    }

    [Fact]
    public async Task A_file_that_is_not_a_database_is_not_left_open_by_the_connection_that_was_refused()
    {
        await File.WriteAllBytesAsync(DatabaseFile, Enumerable.Repeat((byte)'x', 4096).ToArray());
        var database = new SqliteDatabase(DatabaseFile, pooling: false);

        Assert.Throws<SqliteException>(database.Open);

        Assert.False(IsHeldOpen(DatabaseFile), "the refused connection still has the file open");
    }

    [Fact]
    public async Task Emptying_the_pool_of_a_database_whose_file_has_gone_leaves_no_stale_handle_for_the_next_open()
    {
        var firstHome = _temp.Join("first");
        var secondHome = _temp.Join("second");
        var link = _temp.Join("link");
        Directory.CreateDirectory(firstHome);
        Directory.CreateDirectory(secondHome);
        CreateDirectoryLink(link, firstHome);
        var database = new SqliteDatabase(Path.Combine(link, "weir.sqlite3"));
        try
        {
            var first = await database.OpenAsync();
            var staleHandle = first.Handle;
            await first.DisposeAsync();

            // The file is gone as far as the database's path goes, while the idle handle in the pool still has it open. A link makes
            // that so on Windows too, where a file that is open cannot be deleted.
            Directory.Delete(link);
            var refused = await Assert.ThrowsAsync<SqliteException>(() => database.OpenAsync());
            Assert.Equal(14, refused.SqliteErrorCode);

            CreateDirectoryLink(link, secondHome);
            await using var afterRefusal = await database.OpenAsync();

            Assert.NotSame(staleHandle, afterRefusal.Handle);
            Assert.True(File.Exists(Path.Combine(secondHome, "weir.sqlite3")));
        }
        finally
        {
            database.ClearPool();
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        // A junction needs no privilege, unlike a symbolic link.
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    /// <summary>Whether some handle of this process has the file open: Windows refuses a second open that shares nothing, Linux lists the descriptors.</summary>
    private static bool IsHeldOpen(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }

        var full = Path.GetFullPath(path);
        return Directory.EnumerateFileSystemEntries("/proc/self/fd")
            .Any(descriptor => string.Equals(File.ResolveLinkTarget(descriptor, returnFinalTarget: false)?.FullName, full, StringComparison.Ordinal));
    }
}
