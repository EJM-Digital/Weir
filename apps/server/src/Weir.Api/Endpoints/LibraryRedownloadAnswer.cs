using Weir.Core.Activity;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Observability;

namespace Weir.Api.Endpoints;

/// <summary>
/// One answer to "Download again" (#509): what the person is told and what Activity keeps. <see cref="ExistingFile"/> and
/// <see cref="Search"/> say what happened at the manager, using only what Weir knows: a delete it could not confirm is
/// <see cref="Unknown"/>, never guessed.
/// </summary>
internal sealed record RedownloadAnswer(
    long LibraryId,
    string FilePath,
    string RequestedBy,
    string Outcome,
    string Message,
    ManagerConnection? Connection = null,
    string? MediaScope = null,
    string ExistingFile = RedownloadAnswer.NotDeleted,
    string Search = RedownloadAnswer.NotSent,
    long? ExistingFileBytes = null)
{
    public const string Requested = "requested";
    public const string DeletedButSearchFailed = "deleted_but_search_failed";
    public const string DeleteNotConfirmed = "delete_not_confirmed";
    public const string FileNotMatched = "file_not_matched";
    public const string Unsupported = "unsupported";
    public const string Failed = "failed";

    public const string Deleted = "deleted";
    public const string NotDeleted = "not_deleted";
    public const string Unknown = "unknown";
    public const string Sent = "sent";
    public const string NotSent = "not_sent";

    public static RedownloadAnswer From(long libraryId, string filePath, string requestedBy, RedownloadResult result, string mediaScope)
    {
        ArgumentNullException.ThrowIfNull(result);
        var answer = new RedownloadAnswer(
            libraryId, filePath, requestedBy, Unsupported, result.Summary, result.Connection, mediaScope, ExistingFileBytes: result.ExistingFileSizeBytes);
        return result.Outcome switch
        {
            RedownloadOutcome.Requested => answer with { Outcome = Requested, ExistingFile = result.DeletedExistingFile ? Deleted : NotDeleted, Search = Sent },
            RedownloadOutcome.DeletedButSearchFailed => answer with { Outcome = DeletedButSearchFailed, ExistingFile = Deleted },
            RedownloadOutcome.DeleteNotConfirmed => answer with { Outcome = DeleteNotConfirmed, ExistingFile = Unknown },
            RedownloadOutcome.FileNotMatched => answer with { Outcome = FileNotMatched },
            _ => answer,
        };
    }

    /// <summary>The Library's note on a title whose file is gone and whose search did not go through.</summary>
    public string WaitingNote() =>
        $"{Connection?.Label ?? "The media manager"} deleted the file, but no search for a new copy went through. Use Download again to search once more.";

    /// <summary>The Activity entry: who asked, which manager, what was deleted, whether a search was sent, and how it ended.</summary>
    public ActivityEventDraft ActivityEvent()
    {
        var result = Outcome switch
        {
            Requested => "success",
            DeletedButSearchFailed or DeleteNotConfirmed or Failed => "failed",
            _ => "skipped",
        };
        var detail = OperatorMessages.ActivityDetailEnvelope(
                "library", "redownload", "manual", result, Connection?.Kind, MediaScope, userMessage: Message)
            .Set("library_id", LibraryId)
            .Set("relative_media_path", FilePath)
            .Set("requested_by", RequestedBy)
            .Set("outcome", Outcome)
            .Set("existing_file", ExistingFile)
            .Set("search", Search);
        if (Connection is not null)
        {
            detail.Set("manager", Connection.Label);
        }

        if (ExistingFileBytes is { } bytes)
        {
            detail.Set("existing_file_bytes", bytes);
        }

        return new ActivityEventDraft(
            LibraryActivityEventTypes.RedownloadRequested,
            "library",
            $"Download again for {System.IO.Path.GetFileName(FilePath)}: {Message}",
            WireJsonWriter.Dumps(detail, WireJsonFormat.Compact));
    }
}
