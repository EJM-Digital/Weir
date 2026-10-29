using Weir.Core.Media;
using Weir.Core.Net;
using Weir.Core.Readiness;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Endpoints;

/// <summary>How many of Weir's own checks pass, out of how many there are.</summary>
public readonly record struct CheckCount(int Passing, int Total);

/// <summary>
/// The checks System counts for "N of M pass", all read from what Weir already knows without testing anything: each part of
/// readiness (the database, the workers and the folder watcher), each switched-on media manager and download client (it passes
/// unless its last test failed), the media tools (ffmpeg and ffprobe are found), and skipping sign-in on the home network (it
/// passes unless it is on with nothing in front of it).
/// </summary>
internal sealed class SystemChecks
{
    private readonly MediaManagerConnectionStore _managers;
    private readonly DownloadClientConnectionStore _downloadClients;
    private readonly IMediaToolResolver _tools;
    private readonly HomeNetworkSignInStore _homeNetwork;

    public SystemChecks(MediaManagerConnectionStore managers, DownloadClientConnectionStore downloadClients, IMediaToolResolver tools, HomeNetworkSignInStore homeNetwork)
    {
        _managers = managers;
        _downloadClients = downloadClients;
        _tools = tools;
        _homeNetwork = homeNetwork;
    }

    public async Task<CheckCount> CountAsync(UnitOfWork uow, ReadinessReport readiness)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(readiness);
        var results = readiness.Steps.Select(step => step.Status == "ready").ToList();
        results.AddRange((await _managers.ListEnabledAsync(uow).ConfigureAwait(false)).Select(manager => manager.LastTestOk != false));
        results.AddRange((await _downloadClients.ListEnabledAsync(uow).ConfigureAwait(false)).Select(client => client.LastTestOk != false));
        results.Add(ToolsAreFound());
        results.Add(HomeNetworkWords.Warning(await _homeNetwork.GetAsync(uow).ConfigureAwait(false), AllowedHostPolicy.Enforced) is null);
        return new CheckCount(results.Count(passed => passed), results.Count);
    }

    private bool ToolsAreFound()
    {
        try
        {
            _tools.Resolve();
            return true;
        }
        catch (MediaToolException)
        {
            return false;
        }
    }
}
