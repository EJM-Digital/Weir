using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// "Download again" (#509) once the manager has been asked to delete: it carries on through the search and the Activity record
/// whatever the browser does. The manager's replies are assumed shapes (<see cref="ArrRedownloadReplies"/>) until the
/// recordings (#1013, #1014) land.
/// </summary>
public sealed class LibraryRedownloadAbortApiTests
{
    [Fact]
    public async Task A_browser_that_goes_away_after_the_delete_is_asked_for_still_gets_the_search_sent_and_the_record_written()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        var deleteAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleteAnswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Manager
            .Json(HttpMethod.Get, RedownloadScripts.MoviePath, ArrRedownloadReplies.MovieWithFile)
            .Route(HttpMethod.Delete, RedownloadScripts.MovieFilePath, async _ =>
            {
                deleteAsked.SetResult();
                await deleteAnswered.Task;
                return new HttpResponseMessage(HttpStatusCode.OK);
            })
            .Json(HttpMethod.Post, "/api/v3/command", ArrRedownloadReplies.CommandAccepted, HttpStatusCode.Created);
        using var request = new HttpRequestMessage(HttpMethod.Post, RedownloadRig.RedownloadsPath(libraryId))
        {
            Content = JsonContent.Create(new { path = RedownloadRig.FilmPath, confirm_destructive = true, csrf_token = await rig.Admin.CsrfAsync() }),
        };
        request.Headers.Add("Cookie", string.Join("; ", rig.Admin.Cookies.Select(pair => $"{pair.Key}={pair.Value}")));
        using var browser = new CancellationTokenSource();
        var sending = rig.Server.Client.SendAsync(request, browser.Token);
        try
        {
            await deleteAsked.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await browser.CancelAsync();
        }
        finally
        {
            // The browser has gone before the manager answers the delete; the answer is always let go, so a failed step cannot leave the server waiting on it.
            deleteAnswered.TrySetResult();
        }

        // However the abandoned request ends (cancelled, or answered after all), it must end.
        await sending.ContinueWith(_ => { }, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(30));
        await Eventually.ThatAsync(() => rig.Manager.Asked().Contains("POST /api/v3/command"), message: "The search was never sent after the browser went away.");
        Assert.Equal(RedownloadScripts.RadarrDeleteThenSearch, rig.Manager.Asked());
        var entry = await Eventually.PollAsync(() => RecordedEntryAsync(rig, libraryId), found => found is not null);
        Assert.NotNull(entry);
        var detail = JsonNode.Parse(entry["detail"]!.GetValue<string>())!;
        Assert.Equal(("requested", "deleted", "sent"), (detail["outcome"]!.GetValue<string>(), detail["existing_file"]!.GetValue<string>(), detail["search"]!.GetValue<string>()));
    }

    private static async Task<JsonNode?> RecordedEntryAsync(RedownloadRig rig, long libraryId)
    {
        using var response = await rig.Admin.GetAsync($"/api/v1/activity/recent?module=library&library_id={libraryId}");
        return (await Json(response))["items"]!.AsArray().SingleOrDefault(item => item!["event_type"]!.GetValue<string>() == "library.redownload_requested");
    }
}
