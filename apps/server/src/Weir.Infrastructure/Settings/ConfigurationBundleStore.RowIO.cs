using System.Globalization;
using Weir.Core.Json;
using Weir.Core.Time;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Settings;

/// <summary>
/// The generic column and row IO both the export and the restore use: reading a table's schema, converting a bundle
/// row into typed values for it, and reading a row back for comparison. Nothing here knows about a specific table.
/// </summary>
public sealed partial class ConfigurationBundleStore
{
    private static IEnumerable<WireValue> Iterate(WireValue value) => value switch
    {
        WireArray list => list.Items,
        WireObject dict => dict.Keys.Select(key => (WireValue)new WireString(key)),
        WireString text => text.Value.Select(c => (WireValue)new WireString(c.ToString())),
        _ => throw new WireTypeException("A section of the backup that holds rows must be a list."),
    };

    private static WireValue Required(WireObject dict, string key) =>
        dict.Get(key) ?? throw new WireTypeException($"The backup is missing {key}.");

    private static long Saturate(System.Numerics.BigInteger value) =>
        value > long.MaxValue ? long.MaxValue : value < long.MinValue ? long.MinValue : (long)value;

    /// <summary>The section's values for the table's own columns (other keys are ignored), with timestamps parsed as ISO 8601.</summary>
    private static List<KeyValuePair<string, WireValue>> ToKwargsList(Dictionary<string, Column> columns, WireObject data)
    {
        var output = new List<KeyValuePair<string, WireValue>>();
        foreach (var (key, raw) in data.Items)
        {
            if (!columns.TryGetValue(key, out var column))
            {
                continue;
            }

            output.Add(new(key, column.Kind == ColumnKind.DateTime ? ParsePyDateTimeValue(raw) : raw));
        }

        return output;
    }

    private static Dictionary<string, WireValue> ToKwargs(Dictionary<string, Column> columns, WireObject data)
    {
        var dict = new Dictionary<string, WireValue>(StringComparer.Ordinal);
        foreach (var (key, value) in ToKwargsList(columns, data))
        {
            dict[key] = value;
        }

        return dict;
    }

    /// <summary>A parsed timestamp is carried as a <see cref="WireTimestampValue"/>.</summary>
    private static WireValue ParsePyDateTimeValue(WireValue raw)
    {
        if (raw is not WireString text)
        {
            return WireNull.Instance;
        }

        var replaced = text.Value.Replace("Z", "+00:00", StringComparison.Ordinal);
        return Timestamp.TryFromIsoFormat(replaced, out var parsed)
            ? new WireTimestampValue(parsed)
            : throw new WireValueException($"Invalid isoformat string: {WireStrings.Repr(replaced)}");
    }

    private static async Task<long> InsertAsync(UnitOfWork uow, string table, Dictionary<string, Column> columns, Dictionary<string, WireValue> kwargs)
    {
        if (kwargs.Count == 0)
        {
            var emptyId = await uow.ExecuteScalarWriteAsync($"INSERT INTO {table} DEFAULT VALUES RETURNING id").ConfigureAwait(false);
            return Convert.ToInt64(emptyId, CultureInfo.InvariantCulture);
        }

        var names = kwargs.Keys.ToList();
        var parameters = names.Select(name => ($"${name}", Bind(columns[name], kwargs[name]))).ToArray();
        var id = await uow.ExecuteScalarWriteAsync(
            $"INSERT INTO {table} ({string.Join(", ", names.Select(n => $"\"{n}\""))}) VALUES ({string.Join(", ", names.Select(n => "$" + n))}) RETURNING id",
            parameters).ConfigureAwait(false);
        return Convert.ToInt64(id, CultureInfo.InvariantCulture);
    }

    private static object? Bind(Column column, WireValue value)
    {
        switch (column.Kind)
        {
            case ColumnKind.DateTime:
                return value is WireTimestampValue dt ? Timestamp.FromUtc(dt.Value.AsUtc).ToSqlite() : DBNull.Value;
            case ColumnKind.Boolean:
                return value switch
                {
                    WireNull => DBNull.Value,
                    WireBool b => b.Value ? 1L : 0L,
                    WireInteger i when i.Value.IsZero || i.Value.IsOne => (long)i.Value,
                    WireNumber f when f.Value is 0.0 or 1.0 => (long)f.Value,
                    _ => throw new WireTypeException($"Not a boolean value: {WireConvert.Repr(value)}"),
                };
            default:
                return WireConvert.ToDatabase(value);
        }
    }

    internal static bool ValuesEqual(ColumnKind kind, WireValue? loaded, WireValue assigned)
    {
        loaded ??= WireNull.Instance;
        if (kind == ColumnKind.DateTime || loaded is WireTimestampValue || assigned is WireTimestampValue)
        {
            return (loaded, assigned) switch
            {
                (WireNull, WireNull) => true,
                (WireTimestampValue a, WireTimestampValue b) => a.Value.IsAware == b.Value.IsAware &&
                    (a.Value.IsAware ? a.Value.AsUtc == b.Value.AsUtc : a.Value.Clock == b.Value.Clock),
                _ => false,
            };
        }

        return (loaded, assigned) switch
        {
            (WireNull, WireNull) => true,
            (WireString a, WireString b) => a.Value == b.Value,
            _ when Numeric(loaded) is { } x && Numeric(assigned) is { } y => x == y,
            _ => false,
        };
    }

    private static double? Numeric(WireValue value) => value switch
    {
        WireBool b => b.Value ? 1 : 0,
        WireInteger i => (double)i.Value,
        WireNumber f => f.Value,
        _ => null,
    };

    private static async Task<Dictionary<string, Column>> ColumnsAsync(UnitOfWork uow, string table)
    {
        var columns = await uow.QueryAsync(
            $"PRAGMA table_info({table})",
            reader => new Column(
                SqliteValues.GetString(reader, 1),
                SqliteValues.GetString(reader, 2).ToUpperInvariant() switch
                {
                    "BOOLEAN" => ColumnKind.Boolean,
                    "DATETIME" => ColumnKind.DateTime,
                    _ => ColumnKind.Raw,
                })).ConfigureAwait(false);
        var ordered = new Dictionary<string, Column>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            ordered[column.Name] = column;
        }

        return ordered;
    }

    /// <summary>Every matching row as a dictionary, columns in table order.</summary>
    private static async Task<List<WireValue>> ReadRowsAsync(UnitOfWork uow, string table, string clause)
    {
        var columns = await ColumnsAsync(uow, table).ConfigureAwait(false);
        var names = columns.Keys.ToList();
        return await uow.QueryAsync<WireValue>(
            $"SELECT {string.Join(", ", names.Select(n => $"\"{n}\""))} FROM {table} {clause}",
            reader =>
            {
                var row = new WireObject();
                for (var i = 0; i < names.Count; i++)
                {
                    row.Set(names[i], SerializeStored(columns[names[i]], reader.GetValue(i)));
                }

                return row;
            }).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, WireValue>?> ReadTypedRowAsync(UnitOfWork uow, string table, Dictionary<string, Column> columns, WireValue pk)
    {
        var names = columns.Keys.ToList();
        var rows = await uow.QueryAsync(
            $"SELECT {string.Join(", ", names.Select(n => $"\"{n}\""))} FROM {table} WHERE id = $pk",
            reader =>
            {
                var row = new Dictionary<string, WireValue>(StringComparer.Ordinal);
                for (var i = 0; i < names.Count; i++)
                {
                    row[names[i]] = LoadStored(columns[names[i]], reader.GetValue(i));
                }

                return row;
            },
            ("$pk", WireConvert.ToDatabase(pk))).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    private static WireValue LoadStored(Column column, object value)
    {
        if (value is DBNull)
        {
            return WireNull.Instance;
        }

        return column.Kind switch
        {
            ColumnKind.Boolean => WireValue.Of(WireConvert.FromDatabase(value).IsTruthy),
            ColumnKind.DateTime => Timestamp.TryFromIsoFormat(Convert.ToString(value, CultureInfo.InvariantCulture)!, out var parsed)
                ? new WireTimestampValue(parsed)
                : throw new FormatException("Invalid isoformat string in the database."),
            _ => WireConvert.FromDatabase(value),
        };
    }

    private static WireValue SerializeStored(Column column, object value)
    {
        var loaded = LoadStored(column, value);
        return loaded is WireTimestampValue dt ? WireValue.Of(Timestamp.FromUtc(dt.Value.AsUtc).IsoFormat()) : loaded;
    }
}
