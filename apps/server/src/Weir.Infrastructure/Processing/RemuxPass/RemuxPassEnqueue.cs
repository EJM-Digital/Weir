using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>The manual enqueue's refusals, with the status and detail the route answers.</summary>
public sealed class RemuxPassEnqueueException : Exception
{
    public RemuxPassEnqueueException()
    {
    }

    public RemuxPassEnqueueException(string message)
        : base(message)
    {
    }

    public RemuxPassEnqueueException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RemuxPassEnqueueException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; } = 400;
}

/// <summary>The work behind <c>POST /processing/jobs/file-remux-pass/enqueue</c>.</summary>
public static class RemuxPassEnqueue
{
    /// <summary>Enqueue one pass, or convert a pending one in place when the operator asks for pass-through. Commits nothing.</summary>
    public static async Task<ProcessingJob> EnqueueManualAsync(
        UnitOfWork uow,
        ProcessingJobStore jobs,
        LibraryStore libraries,
        string relativeMediaPath,
        string mediaScope,
        long? libraryId,
        bool passThroughUnchanged)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(jobs);
        var library = await RemuxPassHandler.ResolveLibraryAsync(uow, libraries, libraryId, mediaScope).ConfigureAwait(false);
        if (libraryId is not null && (library is null || library.Id != libraryId))
        {
            throw new RemuxPassEnqueueException(404, "The selected workflow no longer exists. Refresh Workflows and try again.");
        }

        if (library is null || WireStrings.Strip(library.WatchedFolder ?? string.Empty).Length == 0)
        {
            var label = mediaScope == "tv" ? "TV" : "Movies";
            throw new RemuxPassEnqueueException(
                400,
                $"{label} watched folder is not set in saved path settings. " +
                "Manual processing.file.remux_pass.v1 jobs require it to resolve relative_media_path and for bounded source cleanup. " +
                "Saving path settings does not require a watched folder, but you must configure it before enqueueing this job kind.");
        }

        var relative = WireStrings.Strip(relativeMediaPath);
        var effectiveLibraryId = library.Id;
        var payload = new WireObject()
            .Set("relative_media_path", relative)
            .Set("media_scope", mediaScope)
            .Set("library_id", effectiveLibraryId)
            .Set("pass_through_unchanged", passThroughUnchanged)
            // An operator asked for this, including when it replaces a queued job's choice below.
            .Set("trigger", "manual");
        if (passThroughUnchanged && await PendingJobForPathAsync(uow, relative, effectiveLibraryId).ConfigureAwait(false) is { } pending)
        {
            // Keep hand-off metadata and other additive fields already carried by the queued job.
            WireObject existing;
            try
            {
                existing = WireJsonParser.Parse(string.IsNullOrEmpty(pending.PayloadJson) ? "{}" : pending.PayloadJson) as WireObject ?? new WireObject();
            }
            catch (WireJsonDecodeException)
            {
                existing = new WireObject();
            }

            foreach (var (key, value) in payload.Items)
            {
                existing.Set(key, value);
            }

            var json = WireJsonWriter.Dumps(existing, WireJsonFormat.Compact);
            await uow.ExecuteAsync(
                "UPDATE jobs SET payload_json = $payload, not_before = NULL, updated_at = CURRENT_TIMESTAMP WHERE id = $id",
                ("$payload", json),
                ("$id", pending.Id)).ConfigureAwait(false);
            jobs.AnnounceOnCommit(uow, RemuxPassOutcomes.JobKind);
            jobs.WakeWorkers(RemuxPassOutcomes.JobKind);
            return pending with { PayloadJson = json };
        }

        return await jobs.EnqueueOrGetAsync(
            uow,
            $"{RemuxPassOutcomes.JobKind}:{Guid.NewGuid():N}",
            RemuxPassOutcomes.JobKind,
            WireJsonWriter.Dumps(payload, WireJsonFormat.Compact),
            JobQueueRules.DefaultMaxAttempts,
            runnerCost: null,
            priority: 0).ConfigureAwait(false);
    }

    /// <summary>The oldest pending pass for this path.</summary>
    public static async Task<ProcessingJob?> PendingJobForPathAsync(UnitOfWork uow, string relativePath, long? libraryId)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var wanted = WireStrings.Strip(relativePath ?? string.Empty);
        if (wanted.Length == 0)
        {
            return null;
        }

        var rows = await uow.QueryAsync(
            $"SELECT {ProcessingJobStore.JobColumns} FROM jobs WHERE job_kind = $kind AND status = $pending ORDER BY id",
            ProcessingJobStore.ReadJob,
            ("$kind", RemuxPassOutcomes.JobKind),
            ("$pending", ProcessingJobStatus.Pending)).ConfigureAwait(false);
        foreach (var job in rows)
        {
            if (string.IsNullOrWhiteSpace(job.PayloadJson))
            {
                continue;
            }

            WireValue data;
            try
            {
                data = WireJsonParser.Parse(job.PayloadJson);
            }
            catch (WireJsonDecodeException)
            {
                continue;
            }

            if (data is not WireObject dict || dict.Get("relative_media_path") is not WireString { } path || path.Value != wanted)
            {
                continue;
            }

            if (libraryId is { } id && dict.Get("library_id") is { } jobLibrary and not WireNull && !(jobLibrary is WireInteger number && number.Value == id))
            {
                continue;
            }

            return job;
        }

        return null;
    }
}
