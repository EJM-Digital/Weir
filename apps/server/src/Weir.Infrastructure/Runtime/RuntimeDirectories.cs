using Weir.Core.Configuration;

namespace Weir.Infrastructure.Runtime;

/// <summary>
/// Startup checks on the runtime paths: create the directories Weir writes to and refuse a database
/// path that cannot be used read-write.
/// </summary>
public static class RuntimeDirectories
{
    /// <summary>Create the backup, log and temp directories and the database's parent (idempotent).</summary>
    public static void Ensure(WeirOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.BackupDir);
        Directory.CreateDirectory(options.LogDir);
        Directory.CreateDirectory(options.TempDir);
        // Every path in the options is absolute, so the database always has a folder.
        Directory.CreateDirectory(Path.GetDirectoryName(options.DbPath)!);
    }

    /// <summary>
    /// Fail fast when the SQLite path cannot be used read-write: a directory at that path, a
    /// non-regular file, a parent that does not allow creating files, or an existing file that
    /// cannot be opened for writing. Call after <see cref="Ensure"/>.
    /// </summary>
    public static void AssertSqliteDbLocationUsable(string dbPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(dbPath);
        var resolved = Path.GetFullPath(dbPath);
        if (Directory.Exists(resolved))
        {
            throw new WeirConfigurationException(
                $"SQLite database path must be a file, not a directory: {resolved}. " +
                "Fix WEIR_DB_PATH or remove the directory at that location.");
        }

        // Special files (FIFOs, sockets) have no portable file-type check in .NET, so such a path
        // fails below as "not writable" or at open.
        // A drive root is a directory and was refused above, so the path has a folder.
        var parent = Path.GetDirectoryName(resolved)!;
        try
        {
            var probe = Path.Join(parent, $"tmp{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new WeirConfigurationException(
                $"Cannot create files under database directory (check permissions and disk): {parent}", exception);
        }

        if (File.Exists(resolved))
        {
            try
            {
                using (new FileStream(resolved, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                {
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new WeirConfigurationException(
                    $"SQLite database file exists but is not writable: {resolved}", exception);
            }
        }
    }
}
