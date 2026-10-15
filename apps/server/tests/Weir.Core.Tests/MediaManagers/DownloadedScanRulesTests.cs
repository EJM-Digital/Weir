using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// <see cref="DownloadedScanRules"/>: the command name by scope, the reverse remote-path-mapping translation
/// (<see cref="ArrOsPath.Remap"/> run the other way), and the command body's shape.
/// </summary>
public sealed class DownloadedScanRulesTests
{
    private static List<RemotePathMappingEntry> Mappings(string json) => ManagerSetupRules.ParseMappings(WireJsonParser.Parse(json));

    [Theory]
    [InlineData(MediaManagerKinds.Movie, DownloadedScanRules.MoviesCommand)]
    [InlineData(MediaManagerKinds.Tv, DownloadedScanRules.EpisodesCommand)]
    public void The_command_name_follows_the_arr_scope(string arrScope, string expected) =>
        Assert.Equal(expected, DownloadedScanRules.CommandName(arrScope));

    [Fact]
    public void A_mapping_whose_local_path_contains_the_output_path_is_translated_to_its_remote_path()
    {
        var mappings = Mappings("""[{"host":"qbittorrent","remotePath":"/media/downloads/complete/","localPath":"/media/downloads/weir/","id":1}]""");

        var translated = DownloadedScanRules.TranslateOutputPath("/media/downloads/weir/tv/Show.S01E01.mkv", mappings);

        Assert.Equal("/media/downloads/complete/tv/Show.S01E01.mkv", translated);
    }

    [Fact]
    public void A_mapping_onto_the_managers_filesystem_root_keeps_the_path_below_it()
    {
        var mappings = Mappings("""[{"host":"qbittorrent","remotePath":"/","localPath":"/media/downloads/weir","id":1}]""");

        Assert.Equal("/tv/Show.S01E01.mkv", DownloadedScanRules.TranslateOutputPath("/media/downloads/weir/tv/Show.S01E01.mkv", mappings));
    }

    [Fact]
    public void The_first_matching_mapping_in_the_managers_own_order_wins()
    {
        var mappings = Mappings("""
            [
              {"host":"a","remotePath":"/complete/a","localPath":"/weir/output","id":1},
              {"host":"b","remotePath":"/complete/b","localPath":"/weir/output","id":2}
            ]
            """);

        var translated = DownloadedScanRules.TranslateOutputPath("/weir/output/movie.mkv", mappings);

        Assert.Equal("/complete/a/movie.mkv", translated);
    }

    [Fact]
    public void No_matching_mapping_leaves_the_path_unchanged()
    {
        var mappings = Mappings("""[{"host":"qbittorrent","remotePath":"/media/downloads/complete","localPath":"/media/downloads/other","id":1}]""");

        var translated = DownloadedScanRules.TranslateOutputPath("/media/downloads/weir/movie.mkv", mappings);

        Assert.Equal("/media/downloads/weir/movie.mkv", translated);
    }

    [Fact]
    public void No_mappings_at_all_leaves_the_path_unchanged() =>
        Assert.Equal("/media/downloads/weir/movie.mkv", DownloadedScanRules.TranslateOutputPath("/media/downloads/weir/movie.mkv", []));

    [Fact]
    public void An_unrooted_output_path_is_never_translated()
    {
        var mappings = Mappings("""[{"host":"qbittorrent","remotePath":"complete","localPath":"weir","id":1}]""");

        Assert.Equal("weir/movie.mkv", DownloadedScanRules.TranslateOutputPath("weir/movie.mkv", mappings));
    }

    [Fact]
    public void The_command_body_names_the_scope_the_translated_path_and_move_import_mode()
    {
        var body = DownloadedScanRules.CommandBody(MediaManagerKinds.Tv, "/complete/tv/Show.S01E01.mkv", null);

        Assert.Equal("DownloadedEpisodesScan", WireConvert.Str(body["name"]));
        Assert.Equal("/complete/tv/Show.S01E01.mkv", WireConvert.Str(body["path"]));
        Assert.Equal("Move", WireConvert.Str(body["importMode"]));
        Assert.False(body.ContainsKey("downloadClientId"));
    }

    [Fact]
    public void A_known_download_client_id_is_included_and_an_unknown_one_is_omitted_entirely()
    {
        var known = DownloadedScanRules.CommandBody(MediaManagerKinds.Movie, "/complete/movie.mkv", 7);
        var unknown = DownloadedScanRules.CommandBody(MediaManagerKinds.Movie, "/complete/movie.mkv", null);

        Assert.Equal(7L, (long)((WireInteger)known["downloadClientId"]).Value);
        Assert.False(unknown.ContainsKey("downloadClientId"));
    }
}
