using Weir.Core.Media;
using Weir.Core.Readiness;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Endpoints;

/// <summary>How many of Weir's own checks pass, out of how many there are.</summary>
public readonly record struct CheckCount(int Passing, int Total);

/// <summary>
/// The checks System counts for "N of M pass", all read from what Weir already knows without testing anything: each part of
/// readiness (the database, the workers and the folder watcher), each switched-on media manager and download client (it passes
/// unless its last test failed), and the media tools (ffmpeg and ffprobe are found).
/// </summary>
internal sealed class SystemChecks
{
    private readonly MediaManagerConnectionStore _managers;
    private readonly DownloadClientConnectionStore _downloadClients;
    private readonly IMediaToolResolver _tools;

    public SystemChecks(MediaManagerConnectionStore managers, DownloadClientConnectionStore downloadClients, IMediaToolResolver tools)
    {
        _managers = managers;
        _downloadClients = downloadClients;
        _tools = tools;
    }

    public async Task<CheckCount> CountAsync(UnitOfWork uow, ReadinessReport readiness)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(readiness);
        var results = readiness.Steps.Select(step => step.Status == "ready").ToList();
        results.AddRange((await _managers.ListEnabledAsync(uow).ConfigureAwait(false)).Select(manager => manager.LastTestOk != false));
        results.AddRange((await _downloadClients.ListEnabledAsync(uow).ConfigureAwait(false)).Select(client => client.LastTestOk != false));
        results.Add(ToolsAreFound());
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
