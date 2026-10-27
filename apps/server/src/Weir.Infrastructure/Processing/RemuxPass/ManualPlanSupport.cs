using System.Text.Json;
using Weir.Core.Configuration;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Core.Rules;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>The manual-plan endpoints' refusals, with the status and detail the route answers (issue #501).</summary>
public sealed class ManualPlanEnqueueException : Exception
{
    public ManualPlanEnqueueException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

/// <summary>What both the tracks listing and the manual-plan submission need: the file, its library, a fresh probe of
/// the held source and the rules that would otherwise apply (issue #501).</summary>
public sealed record ManualPlanFileContext(
    ProcessingFileRecord File,
    ProcessingLibraryRecord Library,
    ProcessingPathRuntime Runtime,
    ProcessingRulesConfig Rules,
    string SourcePath,
    ProbeResult Probe);

/// <summary>Loading a held file's fresh probe and rules for the manual-plan endpoints (issue #501).</summary>
public static class ManualPlanSupport
{
    public static async Task<ManualPlanFileContext> LoadAsync(
        UnitOfWork uow, FileStateStore files, LibraryStore libraries, MediaTools mediaTools, WeirOptions options, long fileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(mediaTools);
        ArgumentNullException.ThrowIfNull(options);

        var file = await files.GetAsync(uow, fileId).ConfigureAwait(false)
            ?? throw new ManualPlanEnqueueException(404, "Weir has no record of that file.");
        // A file row cannot outlive its workflow (files.library_id cascades on delete), and both reads are in one transaction.
        var library = (await libraries.GetAsync(uow, file.LibraryId).ConfigureAwait(false))!;

        var (runtime, problem) = RemuxPassPaths.RuntimeForLibrary(library, options.WeirHome, await libraries.ManagerLinksAsync(uow, library.Id).ConfigureAwait(false));
        if (runtime is null)
        {
            throw new ManualPlanEnqueueException(400, problem!);
        }

        string source;
        try
        {
            source = RemuxPassPaths.ResolveMediaFileUnderRoot(runtime.WatchedFolder, file.RelativePath);
        }
        catch (ArgumentException exception)
        {
            throw new ManualPlanEnqueueException(400, exception.Message);
        }

        if (!System.IO.File.Exists(source))
        {
            throw new ManualPlanEnqueueException(404, "Weir could not find this file under the saved watched folder.");
        }

        JsonElement probeJson;
        try
        {
            probeJson = await mediaTools.FfprobeJsonAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Any probe failure is the operator's 400, not a server error.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            throw new ManualPlanEnqueueException(400, ToolFailureText.ForFailure(exception));
        }

        var rules = library.RuleSetId is { } ruleSetId && await libraries.GetRuleSetAsync(uow, ruleSetId).ConfigureAwait(false) is { } ruleSet
            ? RemuxPassPaths.RulesConfigFor(ruleSet)
            : RemuxRules.DefaultConfig();

        return new ManualPlanFileContext(file, library, runtime, rules, source, new ProbeResult(probeJson));
    }

    /// <summary>Enqueue a remux pass carrying the operator's choice and the fingerprint taken at submission. Commits nothing.</summary>
    public static async Task<ProcessingJob> EnqueueManualPlanAsync(
        UnitOfWork uow, ProcessingJobStore jobs, ManualPlanFileContext context, ManualPlanChoice choice, SourceFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(choice);

        var payload = new WireObject()
            .Set("relative_media_path", context.File.RelativePath)
            .Set("media_scope", context.Library.MediaType)
            .Set("library_id", context.Library.Id)
            .Set("trigger", "manual")
            .Set("manual_plan", ManualPlanJson.ToPyDict(choice))
            .Set("source_fingerprint", ManualPlanJson.ToPyDict(fingerprint));

        return await jobs.EnqueueOrGetAsync(
            uow,
            $"{RemuxPassOutcomes.JobKind}:manual-plan:{context.File.Id}:{Guid.NewGuid():N}",
            RemuxPassOutcomes.JobKind,
            WireJsonWriter.Dumps(payload, WireJsonFormat.Compact),
            JobQueueRules.DefaultMaxAttempts,
            runnerCost: null,
            priority: 0).ConfigureAwait(false);
    }
}
