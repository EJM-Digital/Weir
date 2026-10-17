namespace Weir.Core.MediaManagers;

/// <summary>What happened when a manager was asked to redownload a title's file (#509).</summary>
public enum RedownloadOutcome
{
    /// <summary>The existing file (when there was one) was deleted and a search was dispatched.</summary>
    Requested,

    /// <summary>
    /// The existing file was deleted, but dispatching the search itself failed. The most dangerous
    /// outcome this interface can report: the file is already gone and no replacement was ever requested.
    /// The caller should mark the title waiting and tell the operator plainly, not just log it.
    /// </summary>
    DeletedButSearchFailed,

    /// <summary>This manager/kind cannot be asked at all (unverified capability, or no manager).</summary>
    Unsupported,

    /// <summary>
    /// The manager lists no file at the path Weir has, so there is nothing Weir can delete. Nothing was deleted and no search was
    /// sent: searching anyway would fetch a second copy beside a file Weir could not remove.
    /// </summary>
    FileNotMatched,

    /// <summary>
    /// Asking the manager to delete the file timed out, could not be sent, or got a server error back. The file may or may not be
    /// gone, so no search was sent and the person is asked to check the manager.
    /// </summary>
    DeleteNotConfirmed,
}

/// <summary>
/// One manager's answer to a redownload request (#509, step 3). <see cref="DeletedExistingFile"/> is the
/// explicit destructive-step flag the issue requires: true whenever Weir told the manager to delete a file
/// from disk before any replacement existed, so the UI can state that plainly in its confirmation, not just
/// imply it. <see cref="ExistingFileSizeBytes"/> is the manager's own record of the file it deleted (or was
/// about to delete), offered back so a confirmation dialog can quote "about this much to download again"
/// without a second round trip.
/// </summary>
public sealed record RedownloadResult(
    ManagerConnection Connection,
    RedownloadOutcome Outcome,
    bool DeletedExistingFile,
    long? ExistingFileSizeBytes,
    string Summary,
    string? Detail = null);

/// <summary>
/// Asks a manager to redownload one title's file after Weir removed a track for good (issue #509): the
/// honest alternative to restoring a track that is gone. Only implemented where the product's own
/// behaviour has been verified from source (Sonarr/Radarr — see <c>Weir.Infrastructure.MediaManagers
/// .ArrManagerRedownload</c>'s remarks for the citations); every other kind, and no manager at all, answers
/// <see cref="RedownloadOutcome.Unsupported"/> rather than guessing at an unverified API.
/// </summary>
public interface IManagerRedownload
{
    /// <summary>Whether a connection of this kind can be asked at all. No network; used before ever dialing out.</summary>
    bool SupportsRedownload(string? kind);

    /// <summary>
    /// Ask <paramref name="connection"/> to redownload the file at <paramref name="filePath"/> for the title
    /// <paramref name="titleId"/> (the id <see cref="IMediaManagerPort.ListLibraryFilesAsync"/> matched).
    /// Never throws for a plain "this manager cannot do this"; throws <see cref="MediaManagerHttpException"/>
    /// or <see cref="MediaManagerUnreachableException"/> only when a call failed before it could have deleted
    /// anything (or the manager refused the delete). Once a delete may have gone through, the failure is reported
    /// instead of thrown, since a caller must not lose track of a file that may be gone: as
    /// <see cref="RedownloadOutcome.DeletedButSearchFailed"/> when the delete succeeded and the search failed, and as
    /// <see cref="RedownloadOutcome.DeleteNotConfirmed"/> when the delete itself may or may not have happened.
    /// </summary>
    Task<RedownloadResult> RequestRedownloadAsync(
        ManagerConnection connection,
        string mediaScope,
        string titleId,
        string filePath,
        CancellationToken cancellationToken = default);
}

/// <summary>The pure, connection-independent rules behind redownload gating and messaging (#509).</summary>
public static class ManagerRedownloadRules
{
    /// <summary>Shown when there is no connected manager at all: the "no-manager" path in the issue.</summary>
    public const string NoManagerMessage =
        "No connected media manager can be asked to download this again. Download it again yourself.";

    /// <summary>
    /// Sonarr/Radarr only (issue #509's verified behaviour). Deluno and a bare "native" connection are
    /// deliberately excluded: Deluno's external API has no "search again and replace this file" capability
    /// as of the checkout read for this issue (Deluno's repository, commit confirmed in
    /// <c>ArrManagerRedownload</c>'s remarks) — a Deluno issue is needed before this can be verified and
    /// turned on, so it stays unsupported rather than guessed at.
    /// </summary>
    public static bool KindSupportsRedownload(string? kind) =>
        ManagerKindProfiles.ForKind(kind)?.IsArr == true;

    /// <summary>
    /// Whether "Download again" can be offered for a library-mode file (issue #551, feeding #509's
    /// <c>can_redownload</c>): a manager kind issue #509 verified (Sonarr/Radarr) <em>and</em> a scan actually
    /// matched the file to one of that manager's own titles — <paramref name="managerConnectionId"/> and
    /// <paramref name="managerTitleId"/> are #551's <c>LibraryScanFileEntry</c> fields, present together or not
    /// at all. Without a match there is no manager file to act on, however capable the kind is in principle.
    /// </summary>
    public static bool CanRedownload(string? managerKind, long? managerConnectionId, string? managerTitleId) =>
        KindSupportsRedownload(managerKind) && managerConnectionId is not null && !string.IsNullOrEmpty(managerTitleId);

    /// <summary>
    /// The sentence a UI must show before running a redownload: it must state that the current file is
    /// replaced, that removal happens before a replacement exists, and that there is no guarantee a
    /// matching release will be found (issue #509's explicit-request confirmation requirements).
    /// </summary>
    public static string DestructiveConfirmation(string titleName, long? existingFileSizeBytes)
    {
        var sizeNote = existingFileSizeBytes is { } bytes and > 0
            ? $" (about {FormatBytes(bytes)} to download again)"
            : string.Empty;
        return $"{titleName}'s current file will be deleted before Weir asks for a new one{sizeNote}. " +
               "The new download will be cleaned with today's rules, but there is no guarantee a release " +
               "with the missing track exists — if none is found, this title is left with no file until one is.";
    }

    private static string FormatBytes(long bytes)
    {
        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:0} {units[unit]}"
            : $"{value:0.#} {units[unit]}";
    }
}
