using TimeZoneConverter;

namespace Weir.Core.Time;

/// <summary>Looks up IANA time zone names.</summary>
public interface ITimeZoneResolver
{
    bool TryFind(string name, out TimeZoneInfo zone);
}

/// <summary>The resolver every server component uses: <see cref="TimeZones.TryFind(string?, out TimeZoneInfo)"/>.</summary>
public sealed class IanaTimeZoneResolver : ITimeZoneResolver
{
    public bool TryFind(string name, out TimeZoneInfo zone) => TimeZones.TryFind(name, out zone);
}

/// <summary>IANA time zones, resolved the same way on every platform.</summary>
/// <remarks>
/// Weir runs with invariant globalization, and on Windows that leaves .NET unable to convert an IANA
/// id (the registry holds only Windows ids and the conversion needs ICU). TimeZoneConverter carries the
/// CLDR mapping and the IANA links, so the same names resolve on every platform. Only IANA names are
/// accepted: a Windows id such as <c>Eastern Standard Time</c> is unknown, so a saved setting means the
/// same zone wherever the server runs.
/// </remarks>
public static class TimeZones
{
    /// <summary>The zone for an exact IANA name; false for an unknown or malformed name.</summary>
    public static bool TryFind(string? name, out TimeZoneInfo zone) =>
        TryFind(name, static (string id, out TimeZoneInfo found) => TZConvert.TryGetTimeZoneInfo(id, out found!), out zone);

    /// <summary>Reads a zone from the system's time zone data; false when the data does not have it.</summary>
    internal delegate bool ZoneLookup(string name, out TimeZoneInfo zone);

    /// <summary><see cref="TryFind(string?, out TimeZoneInfo)"/> with the system's data passed in, which a host without a zone's data stands in for.</summary>
    internal static bool TryFind(string? name, ZoneLookup lookup, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrEmpty(name) || name.Contains('\0', StringComparison.Ordinal) || !TZConvert.KnownIanaTimeZoneNames.Contains(name))
        {
            return false;
        }

        if (lookup(name, out var found))
        {
            zone = found;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The zone for a trimmed name, with a missing, empty or unknown name meaning UTC, so a bad setting
    /// never stops a schedule.
    /// </summary>
    public static TimeZoneInfo Find(string? name)
    {
        var id = (name ?? "UTC").Trim();
        if (id.Length == 0 || id == "UTC")
        {
            return TimeZoneInfo.Utc;
        }

        return TryFind(id, out var zone) ? zone : TimeZoneInfo.Utc;
    }

    public static DateTimeOffset ToLocal(DateTimeOffset now, string? name) => TimeZoneInfo.ConvertTime(now, Find(name));

    /// <summary>The moment the local day containing <paramref name="now"/> began, in the zone named <paramref name="name"/>.</summary>
    public static DateTimeOffset StartOfLocalDay(DateTimeOffset now, string? name)
    {
        var zone = Find(name);
        return FromWallClock(TimeZoneInfo.ConvertTime(now, zone).Date, zone);
    }

    /// <summary>
    /// A wall-clock time in <paramref name="zone"/> as an instant. For an ambiguous or skipped time
    /// this takes the first offset, the one in force before the transition.
    /// </summary>
    public static DateTimeOffset FromWallClock(DateTime wall, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        TimeSpan offset;
        if (zone.IsAmbiguousTime(wall))
        {
            offset = zone.GetAmbiguousTimeOffsets(wall).Max();
        }
        else if (zone.IsInvalidTime(wall))
        {
            offset = zone.GetUtcOffset(wall.AddHours(-1));
        }
        else
        {
            offset = zone.GetUtcOffset(wall);
        }

        return new DateTimeOffset(wall, offset);
    }
}
