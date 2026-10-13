using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Weir.Infrastructure.Sqlite;

/// <summary>
/// Tells a caller that SQLite is still working, from inside the work. SQLite calls a progress handler every few thousand
/// instructions of the statement it is running (<c>sqlite3_progress_handler</c>), so the handler is called while a large
/// <c>VACUUM INTO</c>, a migration or an integrity check is reading and writing pages, and not while the statement is waiting
/// for a lock or a disk that has stopped answering. That is the difference between a start that is slow and one that is stuck,
/// which a timer on another thread cannot tell.
/// </summary>
internal static class SqliteWorkProgress
{
    /// <summary>
    /// How many virtual-machine instructions pass between two calls. A call costs a transition into managed code, so this is
    /// large; at the speed SQLite runs (tens of millions of instructions a second) it still calls many times a second.
    /// </summary>
    internal const int Instructions = 5_000;

    /// <summary>
    /// Calls <paramref name="working"/> from SQLite while a statement on <paramref name="connection"/> runs, until the result is
    /// disposed. The handler is taken off the connection then, because a pooled connection keeps it for its next user.
    /// </summary>
    internal static IDisposable Report(SqliteConnection connection, Action working)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(working);
        var handle = connection.Handle ?? throw new InvalidOperationException("The connection is not open.");
        delegate_progress callback = _ =>
        {
            working();
            return 0;
        };
        raw.sqlite3_progress_handler(handle, Instructions, callback, null);
        return new Reporting(handle, callback);
    }

    private sealed class Reporting(sqlite3 handle, delegate_progress callback) : IDisposable
    {
        // Held so the delegate lives as long as SQLite can call it.
        private delegate_progress? _callback = callback;

        public void Dispose()
        {
            if (_callback is null)
            {
                return;
            }

            raw.sqlite3_progress_handler(handle, 0, null, null);
            _callback = null;
        }
    }
}
