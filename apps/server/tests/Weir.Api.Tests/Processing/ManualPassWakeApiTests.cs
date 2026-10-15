using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Tests.Platform;
using Weir.Core.Jobs;
using Weir.Infrastructure.Jobs;
using static Weir.Api.Tests.Platform.LiveStreamApiTests;

namespace Weir.Api.Tests.Processing;

/// <summary>Asking for a file to be passed through is picked up by an idle file slot at once, not at the next poll (#1098).</summary>
public sealed class ManualPassWakeApiTests
{
    private const string Enqueue = "/api/v1/processing/jobs/file-remux-pass/enqueue";

    [Fact]
    public async Task Turning_a_queued_pass_into_a_pass_through_wakes_the_file_slots()
    {
        var watched = Directory.CreateTempSubdirectory("weir-wake-pass-").FullName;
        var (server, client) = await StartSignedInAsync();
        await using var _server = server;
        try
        {
            await TestDatabase.ExecuteAsync(server, "UPDATE libraries SET watched_folder = $w WHERE media_type = 'movie'", ("$w", watched));
            // Paused, so no file slot takes the pass before it is changed.
            await TestDatabase.ExecuteAsync(server, "UPDATE suite_settings SET processing_paused = 1 WHERE id = 1");
            using var queued = await client.PostAsync(Enqueue, new { csrf_token = await client.CsrfAsync(), relative_media_path = "wake/queued.mkv" });
            Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
            var wake = server.Services.GetRequiredService<WorkerWakeSignals>().NextWake(WorkLane.Files, slot: int.MaxValue);

            using var changed = await client.PostAsync(
                Enqueue,
                new { csrf_token = await client.CsrfAsync(), relative_media_path = "wake/queued.mkv", pass_through_unchanged = true });

            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            Assert.True(wake.IsCompletedSuccessfully);
        }
        finally
        {
            Directory.Delete(watched, recursive: true);
        }
    }
}
