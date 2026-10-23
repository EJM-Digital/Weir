using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Tests.Media;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>The corners of the Radarr, Sonarr and Deluno ports: a port built with and without a logger, a series Sonarr lists without an id or a name, a rescan with no title to name.</summary>
public sealed class ManagerPortEdgeTests
{
    private static ManagerConnection Connection(string kind) => new(kind, "Main", "http://manager.local", "k", 1);

    [Theory]
    [InlineData("radarr")]
    [InlineData("sonarr")]
    [InlineData("deluno")]
    [InlineData("native")]
    public void A_port_says_which_kind_of_manager_it_speaks_for(string kind)
    {
        var port = new HttpMediaManagerPorts(new FakeManagerHttp(), new ListLogger<HttpMediaManagerPorts>()).PortForKind(kind)!;

        Assert.Equal(kind, port.Kind);
    }

    [Fact]
    public async Task A_port_built_without_a_logger_still_talks_to_its_manager()
    {
        var http = new FakeManagerHttp().Json(HttpMethod.Get, "/api/v3/queue", """{"records":[]}""");
        var port = new HttpMediaManagerPort(ManagerKindProfiles.ForKind("radarr")!, http);

        var signal = await port.QueueRowsAsync(Connection("radarr"));

        Assert.Equal(SignalStatus.Reported, signal.Status);
    }

    [Fact]
    public async Task A_series_sonarr_lists_without_an_id_is_skipped_and_one_without_a_name_is_known_by_its_id()
    {
        var http = new FakeManagerHttp()
            .Json(HttpMethod.Get, "/api/v3/series", """[{"title":"No id"},{"id":3}]""")
            .Json(HttpMethod.Get, "/api/v3/episodefile", """[{"id":30,"path":"/tv/Three/S01E01.mkv"}]""");
        var sonarr = new HttpMediaManagerPorts(http).PortForKind("sonarr")!;

        var signal = await sonarr.ListLibraryFilesAsync(Connection("sonarr"), "tv");

        Assert.Equal(SignalStatus.Reported, signal.Status);
        var file = Assert.Single(signal.Files);
        Assert.Equal("3", file.TitleId);
        Assert.Equal("3", file.TitleName);
        Assert.Equal("/tv/Three/S01E01.mkv", file.FilePath);
        Assert.Equal("seriesId=3", Assert.Single(http.RequestsTo(HttpMethod.Get, "/api/v3/episodefile")).Uri.Query.TrimStart('?'));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("seven")]
    public async Task A_rescan_with_no_usable_title_id_is_refused_before_anything_is_sent(string? titleId)
    {
        var http = new FakeManagerHttp();
        var radarr = new HttpMediaManagerPorts(http).PortForKind("radarr")!;

        var error = await Assert.ThrowsAsync<MediaManagerHttpException>(
            () => radarr.FileChangedAsync(Connection("radarr"), new SortedSet<string>(), titleId, "/media/f.mkv", null, CancellationToken.None));

        Assert.Equal("Radarr (Main) needs a matched title id to rescan /media/f.mkv, and none was given.", error.Message);
        Assert.Empty(http.Requests);
    }
}
