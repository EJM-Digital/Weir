using System.Net;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>The downloaded scan when the manager cannot be asked for its path mappings, or tells Weir to slow down without saying for how long.</summary>
public sealed class ArrDownloadedScanEdgeTests
{
    private static readonly ManagerConnection Radarr = new("radarr", "Radarr", "http://192.0.2.50:7878", "key", 1);

    [Fact]
    public async Task A_manager_that_cannot_be_asked_for_its_mappings_still_gets_the_scan_with_Weirs_own_path()
    {
        var http = new FakeManagerHttp()
            .Throw(HttpMethod.Get, ManagerSetupRules.RemotePathMappingPath, new HttpRequestException("No route to host."))
            .Json(HttpMethod.Post, DownloadedScanRules.CommandPath, """{"id":1,"name":"DownloadedMoviesScan"}""", HttpStatusCode.Created);

        var result = await new ArrDownloadedScan(http).RequestAsync(Radarr, "movie", "/refined/Film/film.mkv", downloadId: null);

        Assert.True(result.Accepted);
        Assert.Contains("/refined/Film/film.mkv", Weir.Core.Json.WireJsonWriter.Dumps(Assert.Single(http.RequestsTo(HttpMethod.Post, DownloadedScanRules.CommandPath)).Json!, Weir.Core.Json.WireJsonFormat.Compact), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manager_that_says_slow_down_without_a_time_is_busy_with_no_time_to_wait()
    {
        var http = new FakeManagerHttp()
            .Json(HttpMethod.Get, ManagerSetupRules.RemotePathMappingPath, "[]")
            .Json(HttpMethod.Post, DownloadedScanRules.CommandPath, "slow down", HttpStatusCode.TooManyRequests);

        var result = await new ArrDownloadedScan(http).RequestAsync(Radarr, "movie", "/refined/Film/film.mkv", downloadId: null);

        Assert.True(result.Busy);
        Assert.False(result.Accepted);
        Assert.Null(result.RetryAfter);
    }
}
