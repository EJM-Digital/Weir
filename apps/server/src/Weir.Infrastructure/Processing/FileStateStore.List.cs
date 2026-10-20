using Weir.Core.Processing;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing;

/// <summary>Listing <c>files</c> rows, one page at a time, in the order a list asks for.</summary>
public sealed partial class FileStateStore
{
    private const string AfterParameterPrefix = "after_";

    /// <summary>How many columns <see cref="Columns"/> names: where a list query's sort key values start.</summary>
    private static readonly int ColumnCount = Columns.Split(',').Length;

    /// <summary>The files matching <paramref name="filter"/>.</summary>
    public Task<List<ProcessingFileRecord>> ListAsync(UnitOfWork uow, ProcessingFileListFilter filter)
    {
        var (sql, parameters) = ListQuery(filter);
        return uow.QueryAsync(sql, Read, parameters);
    }

    /// <summary>
    /// One page of the files matching <paramref name="filter"/>, in the order it asks for, after the file its
    /// <see cref="ProcessingFileListFilter.After"/> names. The page carries the cursor of the next one unless it reaches the last file.
    /// </summary>
    public async Task<ProcessingFilePage> ListPageAsync(UnitOfWork uow, ProcessingFileListFilter filter)
    {
        var (sql, parameters) = ListQuery(filter, filter.ClampedLimit + 1);
        var keys = ProcessingFileOrdering.Keys(filter.Sort, filter.Direction);
        var rows = await uow.QueryAsync(
            sql,
            reader => (File: Read(reader), Key: ProcessingFileOrdering.ReadKey(reader, ColumnCount, keys)),
            parameters).ConfigureAwait(false);
        if (rows.Count <= filter.ClampedLimit)
        {
            return new ProcessingFilePage([.. rows.Select(row => row.File)], null);
        }

        var page = rows.Take(filter.ClampedLimit).ToList();
        return new ProcessingFilePage(
            [.. page.Select(row => row.File)],
            ProcessingFileOrdering.EncodeCursor(filter.Sort, filter.Direction, page[^1].Key));
    }
    internal static (string Sql, (string Name, object? Value)[] Parameters) ListQuery(ProcessingFileListFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return ListQuery(filter, filter.ClampedLimit);
    }

    private static (string Sql, (string Name, object? Value)[] Parameters) ListQuery(ProcessingFileListFilter filter, int rowCount)
    {
        var keys = ProcessingFileOrdering.Keys(filter.Sort, filter.Direction);
        var clauses = new List<string>();
        var parameters = new List<(string, object?)>();
        if (filter.LibraryId is { } libraryId)
        {
            clauses.Add("library_id = @library_id");
            parameters.Add(("@library_id", libraryId));
        }

        if (filter.Statuses is { Count: > 0 } statuses)
        {
            var names = statuses.Select((_, index) => $"@status{index}").ToArray();
            clauses.Add($"status IN ({string.Join(", ", names)})");
            parameters.AddRange(statuses.Select((status, index) => ($"@status{index}", (object?)status)));
        }

        if (!string.IsNullOrEmpty(filter.PathContains))
        {
            clauses.Add("relative_path LIKE @path_contains ESCAPE '\\'");
            parameters.Add(("@path_contains", "%" + SqliteLike.Escape(filter.PathContains) + "%"));
        }

        if (filter.Since is { } since)
        {
            // A file the pass was asked to work on before any scan saw it has no last-seen time yet: it counts from when it was recorded.
            clauses.Add("coalesce(last_seen_at, updated_at, created_at) >= @since");
            parameters.Add(("@since", SqliteValues.ToSqlite(since)));
        }

        if (filter.Ids is { } ids)
        {
            var names = ids.Select((_, index) => $"@id_{index}").ToArray();
            clauses.Add(names.Length == 0 ? "0" : $"id IN ({string.Join(", ", names)})");
            parameters.AddRange(ids.Select((id, index) => ($"@id_{index}", (object?)id)));
        }

        if (filter.After is { } after)
        {
            var (afterSql, afterParameters) = Keyset.After(keys, after, AfterParameterPrefix);
            clauses.Add(afterSql);
            parameters.AddRange(afterParameters);
        }

        var where = clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : string.Empty;
        var keyColumns = string.Join(", ", keys.Select(key => key.Expression));
        return ($"SELECT {Columns}, {keyColumns} FROM files {where}{Keyset.OrderBy(keys)} LIMIT {rowCount}", [.. parameters]);
    }
}
