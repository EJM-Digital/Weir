namespace Weir.Infrastructure.Runtime;

/// <summary>
/// How long a start may be silent while it works on a database of a given size, which the server tells the tray in
/// <see cref="StartupProgress"/> (<c>expect_within_s</c>). Saving a copy of the data and migrating it are mostly SQLite reading
/// and writing pages and report as they go, but they also contain steps SQLite does in one go (syncing the finished copy to disk,
/// the final commit, and the scan an antivirus makes of a large file just written), in which nothing can be reported.
/// </summary>
/// <remarks>
/// The numbers come from timing the start of a 1.5 GB database (300,000 files, 600,000 history rows, 600,000 Activity rows) on the
/// development PC with the CPU oversubscribed twofold (32 busy processes on 16 cores): saving the copy took 140 s with a longest
/// silent stretch of 120 s, and 201 s with a longest silent stretch of 183 s while the disk was also busy writing back another
/// copy of the file. That is 133 s a gigabyte for the whole phase, the worst seen. <see cref="PerGigabyte"/> asks for more than
/// twice that, so a start that is merely slow is waited for, and <see cref="Base"/> is the least that is ever asked for.
/// An idle PC does the same copy in 7 s.
/// </remarks>
public static class StartupPatience
{
    /// <summary>The least a data-heavy step asks for, whatever the size of the database.</summary>
    public static readonly TimeSpan Base = TimeSpan.FromSeconds(30);

    /// <summary>How long more each gigabyte of database asks for.</summary>
    public static readonly TimeSpan PerGigabyte = TimeSpan.FromSeconds(300);

    private const double Gigabyte = 1024d * 1024d * 1024d;

    /// <summary>
    /// How long the server may be silent while it saves a copy of, or migrates, the database at <paramref name="databasePath"/>, from the
    /// size of the file and of its write-ahead log (the data a copy or a migration has to read and write). A database that is not
    /// there yet, or a file that cannot be measured, is asked for as if it were small.
    /// </summary>
    public static TimeSpan ForDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        return ForBytes(SizeOf(databasePath) + SizeOf(databasePath + "-wal"));
    }

    /// <summary>How long the server may be silent while it works on <paramref name="bytes"/> of data.</summary>
    public static TimeSpan ForBytes(long bytes) =>
        Base + TimeSpan.FromSeconds(PerGigabyte.TotalSeconds * Math.Max(0, bytes) / Gigabyte);

    private static long SizeOf(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
