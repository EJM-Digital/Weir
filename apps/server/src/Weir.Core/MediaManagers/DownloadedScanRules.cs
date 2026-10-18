using Weir.Core.Json;

namespace Weir.Core.MediaManagers;

/// <summary>
/// The pure half of the optional downloaded-scan hand-back: for a Sonarr/Radarr connection that does not use
/// Weir's own hand-off protocol, what to call the manager's Downloaded Scan command and where to tell it to look,
/// once a live pass has written a file to the library's output folder.
/// </summary>
public static class DownloadedScanRules
{
    public const string CommandPath = "/api/v3/command";

    public const string MoviesCommand = "DownloadedMoviesScan";
    public const string EpisodesCommand = "DownloadedEpisodesScan";

    /// <summary>Sonarr's and Radarr's own move-in-place import after a scan.</summary>
    public const string ImportMode = "Move";

    /// <summary>The command name for an Arr scope (<see cref="ManagerKindProfile.ArrScope"/>): movies or TV episodes.</summary>
    public static string CommandName(string arrScope) => arrScope == MediaManagerKinds.Tv ? EpisodesCommand : MoviesCommand;

    /// <summary>
    /// Where the manager would look for <paramref name="outputPath"/>, using the first remote path mapping (in the
    /// manager's own order) whose Local Path contains it. This is <see cref="ArrOsPath.Remap"/> in reverse: that
    /// method computes <c>LocalPath + (path - RemotePath)</c> to judge what the manager will do with a path it
    /// reports; here Weir starts from its own local path and wants <c>RemotePath + (path - LocalPath)</c>, so the
    /// mapping's two paths swap roles in the same call. No matching mapping means Weir sends its own path unchanged
    /// — the manager and Weir may still share a filesystem view (the same host, or the same bind mount).
    /// </summary>
    public static string TranslateOutputPath(string outputPath, IReadOnlyList<RemotePathMappingEntry> mappings)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(mappings);
        var local = new ArrOsPath(WireStrings.Strip(outputPath));
        if (!local.IsRooted)
        {
            return outputPath;
        }

        foreach (var mapping in mappings)
        {
            var mappingLocal = new ArrOsPath(mapping.LocalPath);
            if (mappingLocal.IsRooted && mappingLocal.Contains(local))
            {
                return local.Remap(mappingLocal, new ArrOsPath(mapping.RemotePath)).ToString();
            }
        }

        return outputPath;
    }

    /// <summary>
    /// The <c>POST /api/v3/command</c> body. <c>downloadClientId</c> is the download client's own id for the download (the
    /// <c>downloadId</c> its queue shows, such as a torrent's hash), as text: Sonarr and Radarr answer 500 to a number. It is present only
    /// when Weir knows it; a missing field means "not from a download client".
    /// </summary>
    public static WireObject CommandBody(string arrScope, string managerPath, string? downloadId)
    {
        var body = new WireObject()
            .Set("name", CommandName(arrScope))
            .Set("path", managerPath)
            .Set("importMode", ImportMode);
        if (!string.IsNullOrEmpty(downloadId))
        {
            body.Set("downloadClientId", downloadId);
        }

        return body;
    }

    /// <summary>
    /// Sonarr and Radarr keep one Downloaded Scan at a time: asked for a second while the first is queued or running, they answer
    /// 201 with the command that is already there, not with a new one (seen in Sonarr 4.0.20: the reply carries the other scan's id
    /// and path). That is not a scan of <paramref name="requestedPath"/>. Returns true, with the id of the scan in the way when the
    /// manager said it, when the answer is another scan's; false when it is the scan that was asked for, or says nothing about a path.
    /// </summary>
    public static bool IsAnotherScan(WireValue? reply, string requestedPath, out long? otherCommandId)
    {
        otherCommandId = null;
        if (reply is not WireObject command || command.Get("body") is not WireObject body || ManagerValues.Text(body.Get("path")) is not { } answeredPath)
        {
            return false;
        }

        if (LibraryFileChangeRules.PathsEqual(answeredPath, requestedPath))
        {
            return false;
        }

        otherCommandId = ManagerValues.WholeNumber(command.Get("id")) is { } id ? (long)id : null;
        return true;
    }

    /// <summary>Whether a command's <c>GET /api/v3/command/{id}</c> answer says it is over, however it ended.</summary>
    public static bool IsFinished(WireValue? command) =>
        command is WireObject answer && ManagerValues.Text(answer.Get("status")) is "completed" or "failed" or "aborted" or "cancelled" or "orphaned";
}
