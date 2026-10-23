using System.Globalization;
using Microsoft.Data.Sqlite;
using Weir.Core.Jobs;
using Weir.Core.Processing.RemuxPass;
using Weir.Core.Time;

namespace Weir.Infrastructure.Jobs;

/// <summary>Builds <see cref="WorkAdmission"/> from the database; <see cref="WorkAdmissionRules"/> holds the rules.</summary>
public static class WorkAdmissionReader
{
    public static WorkAdmission Evaluate(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(connection);
        SuitePauseSettings? suite = null;
        using (var command = Command(connection, transaction,
                   "SELECT app_timezone, processing_paused, processing_paused_until, scan_while_paused FROM suite_settings WHERE id = 1"))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                suite = new SuitePauseSettings(
                    reader.GetString(0),
                    Bool(reader.GetValue(1)),
                    TimestampColumns.Parse(reader.GetValue(2)) is { } until ? Timestamp.FromUtc(until.UtcDateTime) : null,
                    Bool(reader.GetValue(3)));
            }
        }

        if (suite is null)
        {
            return WorkAdmissionRules.Evaluate(null, null, [], [], now);
        }

        RunnerBudget? budget = null;
        long filesAtOnce = 1;
        var budgetEnabled = true;
        using (var command = Command(connection, transaction,
                   "SELECT runner_capacity, runner_cost_sd, runner_cost_720p, runner_cost_1080p, runner_cost_4k, " +
                   "max_concurrent_files, runner_budget_enabled FROM operator_settings WHERE id = 1"))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                budget = RunnerBudget.FromSettings(Long(reader.GetValue(0)), Long(reader.GetValue(1)), Long(reader.GetValue(2)),
                    Long(reader.GetValue(3)), Long(reader.GetValue(4)));
                filesAtOnce = Long(reader.GetValue(5));
                budgetEnabled = Bool(reader.GetValue(6));
            }
        }

        var leased = new List<LeasedJobSnapshot>();
        using (var command = Command(connection, transaction, "SELECT runner_cost, payload_json, job_kind FROM jobs WHERE status = 'leased'"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                leased.Add(new LeasedJobSnapshot(
                    Long(reader.GetValue(0)),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    WorkLanes.LaneOf(reader.GetString(2))));
            }
        }

        var libraries = new List<LibraryAdmissionSnapshot>();
        using (var command = Command(connection, transaction,
                   "SELECT id, enabled, schedule_enabled, schedule_grid, schedule_hours_limited, schedule_days, schedule_start, " +
                   "schedule_end, max_concurrent_files FROM libraries ORDER BY id"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                libraries.Add(new LibraryAdmissionSnapshot(
                    reader.GetInt64(0),
                    Bool(reader.GetValue(1)),
                    Bool(reader.GetValue(2)),
                    reader.GetString(3),
                    Bool(reader.GetValue(4)),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    Long(reader.GetValue(8))));
            }
        }

        return WorkAdmissionRules.Evaluate(suite, budget, leased, libraries, now, filesAtOnce, budgetEnabled);
    }

    /// <summary>
    /// What is running, what is waiting and which limit the waiting files are waiting on (#633). Read in one
    /// transaction with the same admission the worker's claim uses, so the screen says what the worker would do.
    /// Callers pass a read transaction (<see cref="ProcessingJobStore.ReadAsync{T}"/>), never the write one (#636).
    /// </summary>
    public static FilesAtOnceReadout ReadFilesAtOnce(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now, int workerSlots)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var admission = Evaluate(connection, transaction, now);

        long filesAtOnce = 1;
        var budgetEnabled = true;
        using (var command = Command(connection, transaction, "SELECT max_concurrent_files, runner_budget_enabled FROM operator_settings WHERE id = 1"))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                filesAtOnce = Long(reader.GetValue(0));
                budgetEnabled = Bool(reader.GetValue(1));
            }
        }

        // File work only: upkeep (scans, sweeps) has its own slots and takes nothing from files-at-once, as in Evaluate.
        var running = 0;
        var runningPerLibrary = new Dictionary<long, int>();
        using (var command = Command(connection, transaction, "SELECT payload_json, job_kind FROM jobs WHERE status = 'leased'"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (WorkLanes.LaneOf(reader.GetString(1)) == WorkLane.Upkeep)
                {
                    continue;
                }

                running++;
                if (JobPayload.LibraryIdForAdmission(reader.IsDBNull(0) ? null : reader.GetString(0)) is { } libraryId)
                {
                    runningPerLibrary[libraryId] = runningPerLibrary.GetValueOrDefault(libraryId) + 1;
                }
            }
        }

        // Due file passes only: a job held back with not_before (a retry's backoff, a file waiting out its minimum age)
        // is not waiting on any limit.
        var waiting = new List<WaitingJobSnapshot>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT runner_cost, payload_json FROM jobs WHERE status = 'pending' AND job_kind = @kind " +
                "AND (not_before IS NULL OR julianday(not_before) <= julianday(@now))";
            command.Parameters.AddWithValue("@kind", RemuxPassOutcomes.JobKind);
            command.Parameters.AddWithValue("@now", TimestampColumns.Adapter(now));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                waiting.Add(new WaitingJobSnapshot(Long(reader.GetValue(0)), JobPayload.LibraryIdForAdmission(reader.IsDBNull(1) ? null : reader.GetString(1))));
            }
        }

        var libraries = new List<FilesAtOnceLibrary>();
        using (var command = Command(connection, transaction, "SELECT id, name, enabled, max_concurrent_files FROM libraries ORDER BY id"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                libraries.Add(new FilesAtOnceLibrary(reader.GetInt64(0), reader.GetString(1), Bool(reader.GetValue(2)), Long(reader.GetValue(3))));
            }
        }

        return FilesAtOnceRules.Describe(filesAtOnce, workerSlots, running, runningPerLibrary, waiting, libraries, admission, budgetEnabled);
    }

    internal static bool Bool(object? value) => value switch
    {
        null or DBNull => false,
        long number => number != 0,
        string text => text.Trim() is not ("" or "0"),
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
    };

    internal static long Long(object? value) => value switch
    {
        null or DBNull => 0,
        long number => number,
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
    };

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
