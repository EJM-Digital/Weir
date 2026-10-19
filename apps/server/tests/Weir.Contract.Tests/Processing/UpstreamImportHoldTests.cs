using System.Net;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// A file Radarr still lists as active in its queue is left alone, and Weir says which connection holds it. The answer is built from
/// the queue Radarr reports, matched to the file by its folder.
/// </summary>
[ContractArea("processing")]
public sealed class UpstreamImportHoldTests
{
    private const string Release = "Importing.Movie.2020.1080p";

    [Fact]
    public async Task A_file_radarr_is_still_downloading_is_held_and_the_connection_is_named()
    {
        await using var scenario = await Scenario.StartAsync();
        var (radarr, library) = await scenario.RadarrSetupAsync();
        var source = scenario.WriteRelease(Release, $"{Release}.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng"])));
        radarr.Queue.Add(ArrShapes.QueueRow(
            "radarr",
            ("id", 5151),
            ("downloadId", "SABnzbd_nzo_importing"),
            ("title", Release),
            ("status", "downloading"),
            ("trackedDownloadState", "downloading"),
            ("outputPath", Path.GetDirectoryName(source)),
            ("movieId", 9)));
        var file = await scenario.DetectWithoutQueueingAsync(library, $"{Release}/{Release}.mkv");

        var held = await scenario.Admin.GetAsync($"{Api}/processing/files/{(long)file["id"]!}/why-held");

        Assert.True(held.Status == HttpStatusCode.OK, held.ToString());
        Assert.Equal("wait_upstream", (string)held.Fields["verdict"]!);
        Assert.True((bool)held.Fields["blocked_upstream"]!, held.ToString());
        Assert.Equal($"Radarr on {new Uri(radarr.BaseUrl).Host}", (string)held.Fields["blocked_by_connection"]!);
        Assert.Equal(1, (int)held.Fields["queue_row_count"]!);
    }

    private const string Api = WeirClient.Api;
}
