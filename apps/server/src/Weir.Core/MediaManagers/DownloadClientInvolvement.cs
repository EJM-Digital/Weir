namespace Weir.Core.MediaManagers;

/// <summary>
/// Whether a bare download client connected to Weir feeds a given workflow, so the folder chain only judges a workflow
/// against clients it uses. Weir stores no link between a workflow and a bare client, so involvement is read from what
/// it does know: where the client saves, and which clients the workflow's own Sonarr or Radarr uses. A client that
/// matches neither is left out of the workflow's chain entirely, so a Weir-only workflow, or one of the other media
/// type, is never told that a client it has nothing to do with "would never" deliver to it.
/// </summary>
public static class DownloadClientInvolvement
{
    /// <summary>
    /// True when one of the client's save folders is the watched folder or below it: the workflow is watching where this
    /// client puts finished downloads, as it does when its folder was taken from the client's suggestion. A client that
    /// saves above the watched folder does not count here; a workflow linked to a manager reaches it through
    /// <see cref="IsUsedBy"/>.
    /// </summary>
    public static bool SavesInto(string watchedFolder, DownloadClientFolders folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var watched = new ArrOsPath(watchedFolder.Trim());
        if (!watched.IsRooted)
        {
            return false;
        }

        var saveFolders = folders.CategoryFolders.Select(category => category.Folder).Append(folders.CompletedFolder);
        return saveFolders.Any(folder => !string.IsNullOrEmpty(folder) && watched.Contains(new ArrOsPath(folder)));
    }

    /// <summary>
    /// True when one of the enabled clients a linked Sonarr or Radarr reports is this connection: the same product at the
    /// same host and port. A manager can name the client by an address Weir does not (a container name against an IP), and
    /// then this is false: leaving a client out never raises a false problem, matching it wrongly would.
    /// </summary>
    public static bool IsUsedBy(string kind, string baseUrl, IEnumerable<ArrDownloadClientEntry> managerClients)
    {
        ArgumentNullException.ThrowIfNull(managerClients);
        if (ConnectionEndpoint.Parse(baseUrl) is not { } endpoint)
        {
            return false;
        }

        return managerClients.Any(client =>
            client.Enabled &&
            string.Equals(client.Implementation, kind, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(client.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase) &&
            (client.Port is null || client.Port == endpoint.Port));
    }
}
