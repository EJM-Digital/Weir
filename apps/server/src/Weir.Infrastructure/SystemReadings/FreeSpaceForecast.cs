namespace Weir.Infrastructure.SystemReadings;

/// <summary>
/// Says when a drive will be full at the pace it has been filling, from the free space seen over the last day. A trend
/// needs time to show: with less than <see cref="MinimumSpan"/> of history, or a drive that is not filling, there is no
/// forecast. It is kept in memory, so a restart starts it afresh.
/// </summary>
public sealed class FreeSpaceForecast(TimeProvider time)
{
    /// <summary>The shortest stretch of history a forecast is made from.</summary>
    public static readonly TimeSpan MinimumSpan = TimeSpan.FromHours(1);

    /// <summary>How far back readings are kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>Readings closer together than this add nothing to a trend over hours.</summary>
    public static readonly TimeSpan Spacing = TimeSpan.FromMinutes(5);

    /// <summary>A drive that would take longer than this to fill is not filling in any way worth saying.</summary>
    public const double LongestForecastDays = 365;

    private const int MinimumReadings = 4;
    private const double SecondsPerDay = 86_400;

    private readonly Dictionary<string, List<(DateTimeOffset At, long Free)>> _readings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Notes a drive's free space and returns when it will be full, in days, or null for no forecast.</summary>
    public double? Record(string drive, long freeBytes)
    {
        var now = time.GetUtcNow();
        if (!_readings.TryGetValue(drive, out var readings))
        {
            readings = _readings[drive] = [];
        }

        if (readings.Count == 0 || now - readings[^1].At >= Spacing)
        {
            readings.Add((now, freeBytes));
        }

        readings.RemoveAll(reading => now - reading.At > Retention);
        return DaysUntilFull(readings, freeBytes);
    }

    private static double? DaysUntilFull(List<(DateTimeOffset At, long Free)> readings, long freeNow)
    {
        if (readings.Count < MinimumReadings || readings[^1].At - readings[0].At < MinimumSpan)
        {
            return null;
        }

        // Least-squares slope of free bytes against seconds.
        var origin = readings[0].At;
        var meanX = readings.Average(reading => (reading.At - origin).TotalSeconds);
        var meanY = readings.Average(reading => (double)reading.Free);
        var covariance = readings.Sum(reading => ((reading.At - origin).TotalSeconds - meanX) * (reading.Free - meanY));
        var variance = readings.Sum(reading => Math.Pow((reading.At - origin).TotalSeconds - meanX, 2));
        var bytesPerSecond = covariance / variance;
        if (bytesPerSecond >= 0)
        {
            return null;
        }

        var days = freeNow / -bytesPerSecond / SecondsPerDay;
        return days <= LongestForecastDays ? Math.Round(days, 1) : null;
    }
}
