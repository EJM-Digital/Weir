using Weir.Core.Paging;

namespace Weir.Core.Logs;

/// <summary>What System › Logs can be ordered by.</summary>
public enum SystemLogSort
{
    /// <summary>When the row happened.</summary>
    Time,

    /// <summary>How the row went, by severity: errors first when ascending.</summary>
    Level,

    /// <summary>Where the row came from, by the source's word.</summary>
    Source,

    /// <summary>What the row is about, by the category's word.</summary>
    Category,

    /// <summary>The name of the workflow the row belongs to, ignoring the case of its letters. A row with no workflow comes last either way.</summary>
    Workflow,
}

/// <summary>The wire names of <see cref="SystemLogSort"/>, as a <c>sort</c> query parameter spells them.</summary>
public static class SystemLogSorts
{
    public const string Time = "time";
    public const string Level = "level";
    public const string Source = "source";
    public const string Category = "category";
    public const string Workflow = "workflow";

    public static readonly IReadOnlyList<string> All = [Time, Level, Source, Category, Workflow];

    public static string NameOf(SystemLogSort sort) => sort switch
    {
        SystemLogSort.Level => Level,
        SystemLogSort.Source => Source,
        SystemLogSort.Category => Category,
        SystemLogSort.Workflow => Workflow,
        _ => Time,
    };

    /// <summary>The sort a wire name stands for, or false when it is not one of <see cref="All"/>.</summary>
    public static bool TryParse(string? name, out SystemLogSort sort)
    {
        sort = name switch
        {
            Level => SystemLogSort.Level,
            Source => SystemLogSort.Source,
            Category => SystemLogSort.Category,
            Workflow => SystemLogSort.Workflow,
            _ => SystemLogSort.Time,
        };
        return name is Time or Level or Source or Category or Workflow;
    }
}

/// <summary>One value of a row's place in the log.</summary>
public enum SystemLogKeyPart
{
    /// <summary>The level's place in <see cref="SystemLogLevels.All"/>.</summary>
    LevelRank,

    /// <summary>The source's word.</summary>
    SourceName,

    /// <summary>The category's word.</summary>
    CategoryName,

    /// <summary>1 when the row has no workflow name, else 0.</summary>
    WorkflowMissing,

    /// <summary>The workflow's name.</summary>
    WorkflowName,

    /// <summary>When the row happened, in ticks (UTC).</summary>
    At,

    /// <summary>The source, by its number.</summary>
    Source,

    /// <summary>The row's number in its source.</summary>
    Number,
}

/// <summary>
/// The order System › Logs is listed and paged in. A row's place is its key: the values of the sort's own parts, then when
/// it happened, its source and its number in the source, which no other row shares. Every part runs the way the list does,
/// so ties on the sort's value fall newest first when the list is descending and oldest first when ascending, and the
/// whole list reads backwards when its direction flips. The one exception is a missing workflow, which comes last either way.
/// </summary>
public sealed class SystemLogOrder
{
    private static readonly SystemLogKeyPart[] TimeParts = [SystemLogKeyPart.At, SystemLogKeyPart.Source, SystemLogKeyPart.Number];

    public SystemLogOrder(SystemLogSort sort, SortDirection direction)
    {
        Sort = sort;
        Direction = direction;
        Parts = [.. LeadingParts(sort), .. TimeParts];
        Shape = [.. Parts.Select(part => new KeysetPart(KindOf(part), DirectionOf(part, direction)))];
    }

    /// <summary>The names for rows that have no workflow to look up, such as the lines of the server log.</summary>
    public static IReadOnlyDictionary<long, string> NoWorkflowNames { get; } = new Dictionary<long, string>();

    /// <summary>Newest first: the order the log has when it is not asked for another.</summary>
    public static SystemLogOrder Newest { get; } = new(SystemLogSort.Time, SortDirection.Descending);

    public SystemLogSort Sort { get; }

    public SortDirection Direction { get; }

    public IReadOnlyList<SystemLogKeyPart> Parts { get; }

    /// <summary>What each part of a key is, and which way it runs, in the order of <see cref="Parts"/>.</summary>
    public IReadOnlyList<KeysetPart> Shape { get; }

    /// <summary>The row's key. A workflow has a name only when <paramref name="workflowNames"/> knows its id.</summary>
    public IReadOnlyList<object?> KeyOf(SystemLogRow row, IReadOnlyDictionary<long, string> workflowNames)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(workflowNames);
        string? workflowName = row.WorkflowId is { } id && workflowNames.TryGetValue(id, out var name) ? name : null;
        return [.. Parts.Select(part => ValueOf(part, row, workflowName))];
    }

    /// <summary>Negative when the row with key <paramref name="first"/> comes before the one with <paramref name="second"/>, positive when after.</summary>
    public int Compare(IReadOnlyList<object?> first, IReadOnlyList<object?> second) => KeysetOrder.Compare(first, second, Shape);

    public string EncodeCursor(IReadOnlyList<object?> key) => KeysetCursor.Encode(SystemLogSorts.NameOf(Sort), Direction, key);

    /// <summary>The key a cursor stands for, or false when it is not one this log gave out under this order.</summary>
    public bool TryDecodeCursor(string? text, out IReadOnlyList<object?> key) =>
        KeysetCursor.TryDecode(text, SystemLogSorts.NameOf(Sort), Direction, Shape, out key) && HasReadableValues(key);

    /// <summary>The key of a cursor this log gave out under this order, which it can always read back; one that is not such a cursor is an error.</summary>
    public IReadOnlyList<object?> KeyOfGivenCursor(string cursor) =>
        TryDecodeCursor(cursor, out var key) ? key : throw new InvalidOperationException("The log gave out a cursor for its next page that it cannot read back.");

    private bool HasReadableValues(IReadOnlyList<object?> key)
    {
        for (var index = 0; index < Parts.Count; index++)
        {
            var readable = Parts[index] switch
            {
                SystemLogKeyPart.At => key[index] is long ticks && ticks >= 0 && ticks <= DateTimeOffset.MaxValue.UtcTicks,
                SystemLogKeyPart.Source => key[index] is long source && source is >= 0 and <= int.MaxValue && Enum.IsDefined((SystemLogSource)source),
                SystemLogKeyPart.Number => key[index] is long,
                _ => true,
            };
            if (!readable)
            {
                return false;
            }
        }

        return true;
    }

    private static SortDirection DirectionOf(SystemLogKeyPart part, SortDirection direction) =>
        part == SystemLogKeyPart.WorkflowMissing ? SortDirection.Ascending : direction;

    private static SystemLogKeyPart[] LeadingParts(SystemLogSort sort) => sort switch
    {
        SystemLogSort.Level => [SystemLogKeyPart.LevelRank],
        SystemLogSort.Source => [SystemLogKeyPart.SourceName],
        SystemLogSort.Category => [SystemLogKeyPart.CategoryName],
        SystemLogSort.Workflow => [SystemLogKeyPart.WorkflowMissing, SystemLogKeyPart.WorkflowName],
        _ => [],
    };

    private static KeysetValueKind KindOf(SystemLogKeyPart part) => part switch
    {
        SystemLogKeyPart.SourceName or SystemLogKeyPart.CategoryName => KeysetValueKind.Text,
        SystemLogKeyPart.WorkflowName => KeysetValueKind.TextIgnoringCase,
        _ => KeysetValueKind.Number,
    };

    private static object? ValueOf(SystemLogKeyPart part, SystemLogRow row, string? workflowName) => part switch
    {
        SystemLogKeyPart.LevelRank => (long)SystemLogLevels.RankOf(row.Level),
        SystemLogKeyPart.SourceName => SystemLogSources.NameOf(row.Source),
        SystemLogKeyPart.CategoryName => row.Category,
        SystemLogKeyPart.WorkflowMissing => workflowName is null ? 1L : 0L,
        SystemLogKeyPart.WorkflowName => workflowName,
        SystemLogKeyPart.At => row.At.UtcTicks,
        SystemLogKeyPart.Source => (long)row.Source,
        _ => row.Key,
    };
}
