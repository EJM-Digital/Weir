using System.Text.Json;
using Microsoft.Playwright;

namespace Weir.E2E.Tests.Support;

/// <summary>
/// Sets up and drives a workflow through Weir's API as the signed-in person in a browser context, so the server makes the
/// change as it would for anyone else, never by a write to its database. Like <see cref="WeirApi"/>, the requests come from the
/// test, not from the page.
/// </summary>
public static class WeirWorkflowApi
{
    private const string Libraries = "/api/v1/processing/libraries";
    private const int StillBusy = 409;

    /// <summary>A new movie workflow with these folders; returns its id. Delete it with <see cref="DeleteAsync"/> when the test is done.</summary>
    public static async Task<long> CreateAsync(IBrowserContext context, string baseUrl, string name, string watchedFolder, string outputFolder)
    {
        await using var response = await context.APIRequest.PostAsync(
            $"{baseUrl}{Libraries}",
            new()
            {
                DataObject = new
                {
                    csrf_token = await CsrfAsync(context, baseUrl),
                    name,
                    media_type = "movie",
                    watched_folder = watchedFolder,
                    output_folder = outputFolder,
                },
            });
        await EnsureOkAsync(response, $"Creating the workflow {name}");
        return JsonDocument.Parse(await response.TextAsync()).RootElement.GetProperty("id").GetInt64();
    }

    /// <summary>Removes the workflow. Weir refuses while a job still belongs to it, as the watcher's look at its folders does for a moment, so this tries again until the job is done.</summary>
    public static async Task DeleteAsync(IBrowserContext context, string baseUrl, long libraryId)
    {
        var giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            await using var response = await context.APIRequest.DeleteAsync(
                $"{baseUrl}{Libraries}/{libraryId}",
                new() { DataObject = new { csrf_token = await CsrfAsync(context, baseUrl) } });
            if (response.Status != StillBusy || DateTime.UtcNow > giveUp)
            {
                await EnsureOkAsync(response, $"Deleting workflow {libraryId}", response.Status == StillBusy ? await JobsAsync(context, baseUrl) : null);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
    }

    /// <summary>The folders the workflow's library scan reads (Library setup on the Library page).</summary>
    public static async Task SetLibraryFoldersAsync(IBrowserContext context, string baseUrl, long libraryId, params string[] folders)
    {
        await using var response = await context.APIRequest.PutAsync(
            $"{baseUrl}{Libraries}/{libraryId}/library-settings",
            new() { DataObject = new { csrf_token = await CsrfAsync(context, baseUrl), library_folders = folders } });
        await EnsureOkAsync(response, $"Setting the library folders of workflow {libraryId}");
    }

    /// <summary>Asks for a library scan, as the Library page's Check again does.</summary>
    public static async Task StartLibraryScanAsync(IBrowserContext context, string baseUrl, long libraryId)
    {
        await using var response = await context.APIRequest.PostAsync(
            $"{baseUrl}{Libraries}/{libraryId}/library-scan",
            new() { DataObject = new { csrf_token = await CsrfAsync(context, baseUrl) } });
        await EnsureOkAsync(response, $"Starting the library scan of workflow {libraryId}");
    }

    /// <summary>Asks for a scan of the workflow's watched folder that only looks and records, as the file watcher's does, and queues nothing.</summary>
    public static async Task StartWatchedFolderScanAsync(IBrowserContext context, string baseUrl, long libraryId)
    {
        await using var response = await context.APIRequest.PostAsync(
            $"{baseUrl}/api/v1/processing/jobs/watched-folder-remux-scan-dispatch/enqueue",
            new()
            {
                DataObject = new
                {
                    csrf_token = await CsrfAsync(context, baseUrl),
                    media_scope = "movie",
                    library_id = libraryId,
                    enqueue_remux_jobs = false,
                },
            });
        await EnsureOkAsync(response, $"Starting the scan of the watched folder of workflow {libraryId}");
    }

    private static async Task<string?> CsrfAsync(IBrowserContext context, string baseUrl)
    {
        await using var csrf = await context.APIRequest.GetAsync($"{baseUrl}/api/v1/auth/csrf");
        return JsonDocument.Parse(await csrf.TextAsync()).RootElement.GetProperty("csrf_token").GetString();
    }

    /// <summary>What the server says its jobs are doing, so a workflow that stays busy names the job that keeps it so.</summary>
    private static async Task<string> JobsAsync(IBrowserContext context, string baseUrl)
    {
        await using var inspection = await context.APIRequest.GetAsync($"{baseUrl}/api/v1/processing/jobs/inspection");
        return $"Jobs at that moment: {await inspection.TextAsync()}";
    }

    private static async Task EnsureOkAsync(IAPIResponse response, string what, string? more = null)
    {
        if (!response.Ok)
        {
            throw new InvalidOperationException($"{what} answered {response.Status}: {await response.TextAsync()}{(more is null ? string.Empty : $"{Environment.NewLine}{more}")}");
        }
    }
}
