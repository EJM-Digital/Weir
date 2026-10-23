using System.Globalization;
using Weir.Core.Json;

namespace Weir.Core.MediaManagers;

/// <summary>
/// The pure half of "does this media manager pick up what this library writes". Read only: nothing here, or in the code
/// that fetches its inputs, ever writes to a manager's settings.
/// </summary>
public static partial class ManagerSetupRules
{
    public const string RemotePathMappingPath = "/api/v3/remotepathmapping";
    public const string DownloadClientPath = "/api/v3/downloadclient";
    public const string DownloadClientConfigPath = "/api/v3/config/downloadclient";

    private const int MaxPort = 65535;

    /// <summary>
    /// <c>GET /api/v3/remotepathmapping</c>: <c>host</c>, <c>remotePath</c>, <c>localPath</c>, camelCased by STJson
    /// (Sonarr develop <c>Sonarr.Api.V3/RemotePathMappings/RemotePathMappingResource.cs</c> L10-12; the same in v5-develop's
    /// v3 API and in Radarr). Kept in the manager's own order: the first mapping that matches wins
    /// (<c>RemotePathMappingService.cs</c> L139-148).
    /// </summary>
    public static List<RemotePathMappingEntry> ParseMappings(WireValue? payload) =>
        [.. ManagerValues.Dicts(payload)
            .Select(row => new RemotePathMappingEntry(
                ManagerValues.FirstText(row, "host") ?? string.Empty,
                ManagerValues.FirstText(row, "remotePath") ?? string.Empty,
                ManagerValues.FirstText(row, "localPath") ?? string.Empty))
            .Where(mapping => mapping.RemotePath.Length > 0)];

    /// <summary>
    /// <c>GET /api/v3/downloadclient</c>: each client's settings arrive as <c>fields[]</c> named by the setting with its first
    /// letter lowercased (<c>Sonarr.Http/ClientSchema/SchemaBuilder.cs</c> L134, L344-347). Every client passes
    /// <c>Settings.Host</c> to the mapping lookup, so <c>host</c> is what a mapping's Host has to match. The category field is
    /// <c>tvCategory</c> in Sonarr and <c>movieCategory</c> in Radarr; a directory, when a client has one, is
    /// <c>tvDirectory</c>/<c>movieDirectory</c> (Transmission, rTorrent) or Deluge's <c>completedDirectory</c>.
    /// </summary>
    public static List<ArrDownloadClientEntry> ParseDownloadClients(WireValue? payload, string mediaScope)
    {
        var categoryField = mediaScope == MediaManagerKinds.Tv ? "tvCategory" : "movieCategory";
        var directoryField = mediaScope == MediaManagerKinds.Tv ? "tvDirectory" : "movieDirectory";
        var clients = new List<ArrDownloadClientEntry>();
        foreach (var row in ManagerValues.Dicts(payload))
        {
            var values = ManagerValues.Dicts(row.Get("fields"))
                .Where(field => ManagerValues.Text(field.Get("name")) is not null)
                .GroupBy(field => ManagerValues.Text(field.Get("name"))!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Get("value"), StringComparer.Ordinal);
            string? FieldText(string name) => ManagerValues.Text(values.GetValueOrDefault(name));
            clients.Add(new ArrDownloadClientEntry(
                ManagerValues.FirstText(row, "name") ?? ManagerValues.FirstText(row, "implementationName") ?? "Download client",
                ManagerValues.FirstText(row, "implementation") ?? string.Empty,
                row.Get("enable") is not WireBool { Value: false },
                FieldText("host"),
                FieldText(categoryField),
                FieldText(directoryField) ?? FieldText("completedDirectory"),
                ManagerValues.FirstText(row, "protocol"),
                PortOf(values.GetValueOrDefault("port"))));
        }

        return clients;
    }

    /// <summary>A client's <c>port</c> field is a number; anything outside the range of a port is treated as not given.</summary>
    private static int? PortOf(WireValue? value) =>
        ManagerValues.WholeNumber(value) is { } number && number > 0 && number <= MaxPort ? (int)number : null;

    /// <summary>
    /// Whether the manager will look for this library's downloads in its output folder. Completed Download Handling
    /// rewrites the path a download client reports with the first mapping whose Host matches the client's Host
    /// (ignoring case) and whose Remote Path contains that path, as <c>LocalPath + (path - RemotePath)</c>
    /// (<c>RemotePathMappingService.cs</c> L139-148). For Weir's output to be the place it looks, a mapping has to
    /// rewrite the watched folder to the output folder.
    /// </summary>
    public static ArrSetupResult EvaluateArr(
        string managerLabel,
        string mediaScope,
        string watchedFolder,
        string outputFolder,
        IReadOnlyList<RemotePathMappingEntry> mappings,
        IReadOnlyList<ArrDownloadClientEntry> clients,
        bool? completedDownloadHandling,
        IReadOnlyList<string>? queueOutputPaths = null,
        bool removesOriginals = false)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(clients);
        var lines = new List<SetupCheckLine>();
        var watched = new ArrOsPath(WireStrings.Strip(watchedFolder));
        var output = new ArrOsPath(WireStrings.Strip(outputFolder));
        var enabled = clients.Where(client => client.Enabled).ToList();
        var hosts = enabled.Select(client => WireStrings.Strip(client.Host ?? string.Empty))
            .Where(host => host.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!watched.IsRooted || !output.IsRooted)
        {
            lines.Add(new SetupCheckLine(SetupCheckLine.Problem, "Set this workflow's watched and output folders first; the mapping is built from them."));
            return new ArrSetupResult(hosts, lines);
        }

        if (completedDownloadHandling == false)
        {
            lines.Add(new SetupCheckLine(
                SetupCheckLine.Problem,
                $"Completed Download Handling is off in {managerLabel}, so it will never import from Weir's output. Turn it on under Setup › Connections › Download clients."));
        }
        else if (completedDownloadHandling == true)
        {
            lines.Add(new SetupCheckLine(SetupCheckLine.Ok, $"Completed Download Handling is on in {managerLabel}."));
        }

        if (enabled.Count == 0)
        {
            lines.Add(new SetupCheckLine(SetupCheckLine.Problem, $"{managerLabel} has no enabled download client, so it has no downloads to import."));
            return new ArrSetupResult(hosts, lines);
        }

        // Weir removing a seeding torrent's files makes qBittorrent report missingFiles, which Sonarr/Radarr read as
        // a warning (Download/Clients/QBittorrent/QBittorrent.cs L281-283) — and Completed Download Handling only imports
        // a download the client reports as completed (Download/CompletedDownloadService.cs L68), so the import never
        // happens. A usenet download is not seeded, so removing it is fine.
        if (removesOriginals && enabled.Any(client => client.IsTorrent))
        {
            lines.Add(new SetupCheckLine(
                SetupCheckLine.Problem,
                $"Your download client seeds torrents. Turn off \"After cleaning, remove the original download\" so seeding keeps working and {managerLabel} can import."));
        }

        lines.AddRange(MappingLines(managerLabel, watched, output, mappings, hosts));
        lines.AddRange(ClientFolderLines(managerLabel, watched, enabled));
        lines.AddRange(QueueLines(managerLabel, watched, output, queueOutputPaths ?? []));
        return new ArrSetupResult(hosts, lines);
    }

    /// <summary>
    /// The live evidence: a queued download's <c>outputPath</c> is the client's path with the mapping already applied
    /// (<c>Queue/QueueService.cs</c> L82 reads the item's <c>OutputPath</c>, which each client remapped, e.g.
    /// <c>QBittorrent.cs</c> L314-318). One under the output folder shows the mapping working; one still under the watched
    /// folder shows it is not being applied to that download.
    /// </summary>
    private static IEnumerable<SetupCheckLine> QueueLines(string managerLabel, ArrOsPath watched, ArrOsPath output, IReadOnlyList<string> queueOutputPaths)
    {
        var paths = queueOutputPaths.Select(path => new ArrOsPath(WireStrings.Strip(path))).Where(path => path.IsRooted).ToList();
        var unmapped = paths.FirstOrDefault(path => watched.Contains(path) && !output.Contains(path));
        if (unmapped.Text is not null)
        {
            yield return new SetupCheckLine(
                SetupCheckLine.Problem,
                $"{managerLabel} is still looking for a download in {unmapped}, inside the watched folder rather than Weir's output, so no mapping is being applied to it.");
        }

        var mapped = paths.Count(path => output.Contains(path));
        if (mapped > 0)
        {
            yield return new SetupCheckLine(
                SetupCheckLine.Ok,
                mapped == 1
                    ? $"A download in {managerLabel}'s queue already points at Weir's output folder."
                    : $"{mapped.ToString(CultureInfo.InvariantCulture)} downloads in {managerLabel}'s queue already point at Weir's output folder.");
        }
    }

    private static IEnumerable<SetupCheckLine> MappingLines(
        string managerLabel, ArrOsPath watched, ArrOsPath output, IReadOnlyList<RemotePathMappingEntry> mappings, List<string> hosts)
    {
        // Every mapping that touches the watched folder: it covers the whole folder (Remote Path at or above it) or part
        // of it (Remote Path inside it, e.g. one category folder).
        var touching = mappings
            .Where(mapping => new ArrOsPath(mapping.RemotePath) is var remote && (remote.Contains(watched) || watched.Contains(remote)))
            .ToList();
        var forClient = touching.Where(mapping => hosts.Contains(WireStrings.Strip(mapping.Host), StringComparer.InvariantCultureIgnoreCase)).ToList();

        if (forClient.Count == 0)
        {
            if (touching.Count > 0 && hosts.Count > 0)
            {
                var mapping = touching[0];
                yield return new SetupCheckLine(
                    SetupCheckLine.Problem,
                    $"{managerLabel} maps {mapping.RemotePath} for the host \"{mapping.Host}\", but its download client's Host is " +
                    $"{Quoted(hosts)}. The mapping's Host has to match it exactly — change it to {Quoted(hosts)}.");
                yield break;
            }

            yield return new SetupCheckLine(
                SetupCheckLine.Problem,
                $"{managerLabel} has no remote path mapping for {watched} yet — add the one above.");
            yield break;
        }

        foreach (var host in hosts.Where(host => !forClient.Any(mapping => string.Equals(WireStrings.Strip(mapping.Host), host, StringComparison.InvariantCultureIgnoreCase))))
        {
            yield return new SetupCheckLine(
                SetupCheckLine.Problem,
                $"The download client at \"{host}\" has no mapping for {watched} — add the same mapping with Host \"{host}\".");
        }

        foreach (var mapping in forClient.GroupBy(mapping => WireStrings.Strip(mapping.Host), StringComparer.InvariantCultureIgnoreCase).Select(group => group.First()))
        {
            var remote = new ArrOsPath(mapping.RemotePath);
            var local = new ArrOsPath(mapping.LocalPath);
            // Where this mapping sends what the client reports, compared with where Weir writes the same thing.
            var (reported, expected) = remote.Contains(watched)
                ? (watched.Remap(remote, local), output)
                : (remote.Remap(remote, local), remote.Remap(watched, output));
            if (reported.SameFolder(expected))
            {
                yield return new SetupCheckLine(
                    SetupCheckLine.Ok,
                    $"{managerLabel} maps {mapping.RemotePath} to {mapping.LocalPath} for \"{mapping.Host}\", so it looks for these downloads in Weir's output folder.");
            }
            else
            {
                yield return new SetupCheckLine(
                    SetupCheckLine.Problem,
                    $"{managerLabel} maps {mapping.RemotePath} to {mapping.LocalPath}, so it will look for these downloads in {reported}, " +
                    $"not in Weir's output folder {expected}. Change that mapping's Local Path to match the one above.");
            }
        }
    }

    private static IEnumerable<SetupCheckLine> ClientFolderLines(string managerLabel, ArrOsPath watched, IReadOnlyList<ArrDownloadClientEntry> clients)
    {
        foreach (var client in clients)
        {
            var directory = WireStrings.Strip(client.Directory ?? string.Empty);
            if (directory.Length > 0 && new ArrOsPath(directory) is var folder && folder.IsRooted)
            {
                yield return watched.Contains(folder)
                    ? new SetupCheckLine(SetupCheckLine.Ok, $"{client.Name} saves {managerLabel}'s downloads to {directory}, inside Weir's watched folder.")
                    : new SetupCheckLine(
                        SetupCheckLine.Problem,
                        $"{client.Name} saves {managerLabel}'s downloads to {directory}, which is not inside Weir's watched folder {watched}. " +
                        "Point one at the other, or Weir will never see them.");
                continue;
            }

            var category = WireStrings.Strip(client.Category ?? string.Empty);
            yield return new SetupCheckLine(
                SetupCheckLine.Unverified,
                category.Length > 0
                    ? $"{client.Name} files {managerLabel}'s downloads under the category \"{category}\", but {managerLabel} does not say where that category saves. " +
                      $"Weir cannot verify its folder is {watched} or inside it. Connect {client.Name} to Weir under Setup › Connections › Media managers to check it."
                    : $"{managerLabel} does not say where {client.Name} saves its downloads. " +
                      $"Weir cannot verify they land in {watched} or inside it. Connect {client.Name} to Weir under Setup › Connections › Media managers to check it.");
        }
    }

    private static string Quoted(List<string> hosts) =>
        string.Join(" or ", hosts.Select(host => string.Create(CultureInfo.InvariantCulture, $"\"{host}\"")));
}
