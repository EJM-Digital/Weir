using System.Globalization;
using System.Text;

namespace Weir.Core.Time;

/// <summary>
/// A date-time as the database and API carry it: a wall-clock value with microsecond precision and an
/// optional UTC offset (naive when <see cref="Offset"/> is <see langword="null"/>).
/// </summary>
public readonly record struct Timestamp(DateTime Clock, TimeSpan? Offset)
{
    /// <summary>The <c>DATETIME</c> text format stored in SQLite; existing rows use it, so it stays fixed.</summary>
    public const string SqliteFormat = "yyyy-MM-dd HH:mm:ss.ffffff";

    public bool IsAware => Offset is not null;

    /// <summary>The current instant as an aware UTC value.</summary>
    public static Timestamp UtcNow(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return FromUtc(time.GetUtcNow().UtcDateTime);
    }

    public static Timestamp FromUtc(DateTime utc) => new(TruncateToMicroseconds(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified)), TimeSpan.Zero);

    public static Timestamp Naive(DateTime clock) => new(TruncateToMicroseconds(DateTime.SpecifyKind(clock, DateTimeKind.Unspecified)), null);

    public static DateTime TruncateToMicroseconds(DateTime value) => new(value.Ticks - (value.Ticks % 10), value.Kind);

    /// <summary>Truncate to whole microseconds, the resolution the database and API keep.</summary>
    public static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) => new(value.Ticks - (value.Ticks % 10), value.Offset);

    /// <summary>An aware value with <paramref name="value"/>'s wall clock and offset.</summary>
    public static Timestamp FromDateTimeOffset(DateTimeOffset value) =>
        new(TruncateToMicroseconds(DateTime.SpecifyKind(value.DateTime, DateTimeKind.Unspecified)), value.Offset);

    /// <summary>The instant, treating a naive value as UTC.</summary>
    public DateTime AsUtc => Offset is { } offset ? DateTime.SpecifyKind(Clock - offset, DateTimeKind.Utc) : DateTime.SpecifyKind(Clock, DateTimeKind.Utc);

    /// <summary>The SQLite text form: the wall clock only, offset dropped.</summary>
    public string ToSqlite() => Clock.ToString(SqliteFormat, CultureInfo.InvariantCulture);

    /// <summary>ISO 8601 with a <c>T</c> separator: microseconds only when non-zero, offset as <c>±HH:MM</c>.</summary>
    public string IsoFormat() => IsoFormat('T');

    /// <summary>ISO 8601 with the given date-time separator.</summary>
    public string IsoFormat(char separator) => ClockText(separator) + OffsetText(zeroAsZ: false);

    /// <summary>The API's JSON form: like <see cref="IsoFormat()"/> but a zero offset is written <c>Z</c>, as existing clients expect.</summary>
    public string ToWireText() => ClockText('T') + OffsetText(zeroAsZ: true);

    private string ClockText(char separator)
    {
        var text = Clock.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + separator + Clock.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var micro = (Clock.Ticks % TimeSpan.TicksPerSecond) / 10;
        return micro == 0 ? text : text + "." + micro.ToString("D6", CultureInfo.InvariantCulture);
    }

    private string OffsetText(bool zeroAsZ)
    {
        if (Offset is not { } offset)
        {
            return string.Empty;
        }

        if (offset == TimeSpan.Zero && zeroAsZ)
        {
            return "Z";
        }

        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        var builder = new StringBuilder(sign);
        builder.Append(abs.Hours.ToString("00", CultureInfo.InvariantCulture)).Append(':')
            .Append(abs.Minutes.ToString("00", CultureInfo.InvariantCulture));
        if (abs.Seconds != 0 || abs.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            builder.Append(':').Append(abs.Seconds.ToString("00", CultureInfo.InvariantCulture));
            var micro = (abs.Ticks % TimeSpan.TicksPerSecond) / 10;
            if (micro != 0)
            {
                builder.Append('.').Append(micro.ToString("D6", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Parses the ISO 8601 forms Weir meets, including timestamps written by earlier releases: <c>YYYY-MM-DD</c> or
    /// <c>YYYYMMDD</c>, optionally a one-character separator and <c>HH[:MM[:SS[.fraction]]]</c>, and an
    /// optional <c>Z</c> or <c>±HH[:MM[:SS[.ffffff]]]</c> offset.
    /// </summary>
    public static bool TryFromIsoFormat(string text, out Timestamp value)
    {
        value = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        int year, month, day, pos;
        if (text.Length >= 10 && text[4] == '-' && text[7] == '-')
        {
            if (!Digits(text, 0, 4, out year) || !Digits(text, 5, 2, out month) || !Digits(text, 8, 2, out day))
            {
                return false;
            }

            pos = 10;
        }
        else if (text.Length >= 8 && Digits(text, 0, 4, out year) && Digits(text, 4, 2, out month) && Digits(text, 6, 2, out day))
        {
            pos = 8;
        }
        else
        {
            return false;
        }

        int hour = 0, minute = 0, second = 0;
        long ticks = 0;
        TimeSpan? offset = null;
        if (pos < text.Length)
        {
            pos++;
            if (!Digits(text, pos, 2, out hour))
            {
                return false;
            }

            pos += 2;
            if (pos < text.Length && text[pos] == ':')
            {
                if (!Digits(text, pos + 1, 2, out minute))
                {
                    return false;
                }

                pos += 3;
                if (pos < text.Length && text[pos] == ':')
                {
                    if (!Digits(text, pos + 1, 2, out second))
                    {
                        return false;
                    }

                    pos += 3;
                    if (pos < text.Length && text[pos] is '.' or ',')
                    {
                        var start = ++pos;
                        while (pos < text.Length && char.IsAsciiDigit(text[pos]))
                        {
                            pos++;
                        }

                        if (pos == start)
                        {
                            return false;
                        }

                        var fraction = text[start..pos];
                        fraction = fraction.Length > 6 ? fraction[..6] : fraction.PadRight(6, '0');
                        ticks = long.Parse(fraction, NumberStyles.None, CultureInfo.InvariantCulture) * 10;
                    }
                }
            }

            if (pos < text.Length)
            {
                if (text[pos] == 'Z' && pos == text.Length - 1)
                {
                    offset = TimeSpan.Zero;
                    pos++;
                }
                else if (text[pos] is '+' or '-')
                {
                    if (!TryParseOffset(text[(pos + 1)..], text[pos] == '-', out var parsed))
                    {
                        return false;
                    }

                    offset = parsed;
                    pos = text.Length;
                }
                else
                {
                    return false;
                }
            }
        }

        // Anything after the time of day was either read above or refused there, so the whole text has been read by now.
        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(Math.Max(1, year), month) ||
            hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        value = new Timestamp(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified).AddTicks(ticks), offset);
        return true;
    }

    private static bool TryParseOffset(string text, bool negative, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        int hours, minutes = 0, seconds = 0;
        long micro = 0;
        if (text.Length == 2 && Digits(text, 0, 2, out hours))
        {
        }
        else if (text.Length == 4 && Digits(text, 0, 2, out hours) && Digits(text, 2, 2, out minutes))
        {
        }
        else if (text.Length >= 5 && text[2] == ':' && Digits(text, 0, 2, out hours) && Digits(text, 3, 2, out minutes))
        {
            if (text.Length > 5)
            {
                if (text.Length < 8 || text[5] != ':' || !Digits(text, 6, 2, out seconds))
                {
                    return false;
                }

                if (text.Length > 8)
                {
                    if (text[8] != '.' || text.Length != 15 || !long.TryParse(text[9..], NumberStyles.None, CultureInfo.InvariantCulture, out micro))
                    {
                        return false;
                    }
                }
            }
        }
        else
        {
            return false;
        }

        if (hours > 23 || minutes > 59 || seconds > 59)
        {
            return false;
        }

        offset = new TimeSpan(0, hours, minutes, seconds).Add(TimeSpan.FromTicks(micro * 10));
        if (negative)
        {
            offset = -offset;
        }

        return true;
    }

    private static bool Digits(string text, int start, int count, out int value)
    {
        value = 0;
        if (start + count > text.Length)
        {
            return false;
        }

        for (var i = start; i < start + count; i++)
        {
            if (!char.IsAsciiDigit(text[i]))
            {
                return false;
            }

            value = (value * 10) + (text[i] - '0');
        }

        return true;
    }
}
