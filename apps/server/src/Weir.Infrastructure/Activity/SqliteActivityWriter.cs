using System.Globalization;
using Microsoft.Data.Sqlite;
using Weir.Core.Activity;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Activity;

/// <summary>
/// Appends <c>activity_events</c> rows: the event, plus the <c>trigger</c>, <c>result</c>, <c>library_id</c>, <c>relative_path</c> and
/// <c>run_key</c> that <see cref="ActivityClassifier"/> lifts from the detail. <c>created_at</c> is the
/// database default. Every write tells the live Activity stream once its transaction commits
/// (<see cref="ActivityNotifications"/>).
/// </summary>
/// <remarks>
/// The one insert for every producer: <see cref="RecordAsync(ActivityEventDraft, CancellationToken)"/>
/// in its own transaction, <see cref="Record"/> inside a caller's raw transaction (the job queue, which
/// reports its commits through <see cref="ActivityNotifications.TransactionCommitted"/>), and
/// <see cref="RecordAsync(UnitOfWork, ActivityEventDraft)"/> inside a unit of work (auth and settings).
/// </remarks>
public sealed class SqliteActivityWriter : IActivityWriter
{
    private const string InsertSql =
        "INSERT INTO activity_events (event_type, module, title, detail, \"trigger\", result, library_id, relative_path, run_key) " +
        "VALUES (@event_type, @module, @title, @detail, @trigger, @result, @library_id, @relative_path, @run_key) RETURNING id";

    private const string SelectSql = "SELECT event_type, title, detail FROM activity_events WHERE activity_events.id = @id";

    private const string UpdateSql =
        "UPDATE activity_events SET event_type=@event_type, title=@title, detail=@detail, \"trigger\"=@trigger, result=@result, " +
        "library_id=@library_id, relative_path=@relative_path, run_key=@run_key WHERE activity_events.id = @id";

    private readonly SqliteDatabase _database;

    public SqliteActivityWriter(SqliteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<long> RecordAsync(ActivityEventDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            var transaction = await uow.WriteTransactionAsync().ConfigureAwait(false);
            var id = Record(uow.Connection, transaction, draft);
            await uow.CommitAsync().ConfigureAwait(false);
            ActivityNotifications.TransactionCommitted(_database, transaction);
            return id;
        }
    }

    /// <summary>Insert inside a caller's transaction.</summary>
    public static long Record(SqliteConnection connection, SqliteTransaction transaction, ActivityEventDraft draft)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(draft);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = InsertSql;
        AddParameters(command, InsertParameters(draft));
        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        ActivityNotifications.Track(transaction, id);
        return id;
    }

    /// <summary>Insert inside a unit of work (which opens its transaction on this first write if needed).</summary>
    public static async Task<long> RecordAsync(UnitOfWork uow, ActivityEventDraft draft)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(draft);
        var id = Convert.ToInt64(await uow.ExecuteScalarWriteAsync(InsertSql, InsertParameters(draft)).ConfigureAwait(false), CultureInfo.InvariantCulture);
        ActivityNotifications.Track(uow, id);
        return id;
    }

    /// <summary>Updates an event inside a unit of work.</summary>
    public static async Task<bool> UpdateAsync(UnitOfWork uow, long activityId, string? eventType = null, string? title = null, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var current = await uow.QuerySingleAsync(SelectSql, ReadDraft, ("@id", activityId)).ConfigureAwait(false);
        if (current is null)
        {
            return false;
        }

        await uow.ExecuteAsync(UpdateSql, UpdateParameters(activityId, Merge(current, eventType, title, detail))).ConfigureAwait(false);
        ActivityNotifications.Track(uow, activityId);
        return true;
    }

    private static ActivityEventDraft ReadDraft(SqliteDataReader reader) =>
        new(SqliteValues.GetString(reader, 0), string.Empty, SqliteValues.GetString(reader, 1), SqliteValues.GetStringOrNull(reader, 2));

    private static ActivityEventDraft Merge(ActivityEventDraft current, string? eventType, string? title, string? detail) =>
        current with { EventType = eventType ?? current.EventType, Title = title ?? current.Title, Detail = detail ?? current.Detail };

    private static void AddParameters(SqliteCommand command, (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private static (string Name, object? Value)[] InsertParameters(ActivityEventDraft draft)
    {
        var facts = ActivityClassifier.Classify(draft.EventType, draft.Detail);
        return
        [
            ("@event_type", draft.EventType),
            ("@module", draft.Module),
            ("@title", draft.Title),
            ("@detail", draft.Detail),
            ("@trigger", facts.Trigger),
            ("@result", facts.Result),
            ("@library_id", facts.LibraryId),
            ("@relative_path", facts.RelativePath),
            ("@run_key", facts.RunKey),
        ];
    }

    private static (string Name, object? Value)[] UpdateParameters(long activityId, ActivityEventDraft draft)
    {
        var facts = ActivityClassifier.Classify(draft.EventType, draft.Detail);
        return
        [
            ("@event_type", draft.EventType),
            ("@title", draft.Title),
            ("@detail", draft.Detail),
            ("@trigger", facts.Trigger),
            ("@result", facts.Result),
            ("@library_id", facts.LibraryId),
            ("@relative_path", facts.RelativePath),
            ("@run_key", facts.RunKey),
            ("@id", activityId),
        ];
    }
}
