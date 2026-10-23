namespace Weir.Infrastructure.Runtime;

/// <summary>
/// How much room Weir's own data takes (the database, its backups, logs and poster cache). Walking the folder costs a read of
/// every file, so the answer is kept for a minute, and callers that arrive while it is being measured share that one walk.
/// </summary>
public sealed class DataFootprint
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(1);

    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private Task<long>? _measurement;
    private DateTimeOffset _measuredAt;

    public DataFootprint(string folder, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        _folder = folder;
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>The size of every file under the folder, as of the last minute.</summary>
    public Task<long> BytesAsync(CancellationToken cancellationToken)
    {
        Task<long> measurement;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_measurement is null || (_measurement.IsCompleted && now - _measuredAt >= FreshFor))
            {
                _measuredAt = now;
                _measurement = Task.Run(() => Measure(_folder), CancellationToken.None);
            }

            measurement = _measurement;
        }

        return measurement.WaitAsync(cancellationToken);
    }

    private static long Measure(string folder)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        long total = 0;
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
            {
                total += LengthOrZero(file);
            }
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
        }

        return total;
    }

    /// <summary>A file that was deleted or locked since it was listed counts for nothing.</summary>
    internal static long LengthOrZero(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
