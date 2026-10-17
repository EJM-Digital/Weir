using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Setup › Workflows › File paths, the editor: every setting a person can change is typed, picked or toggled in the real editor, goes
/// to the server exactly as chosen, is stored, and shows again when the workflow is opened afterwards.
/// </summary>
public sealed class WorkflowEditorSettingsTests(E2EServer server) : E2ETestBase(server)
{
    private const string Name = "E2E every setting";
    private const string ProfileName = "E2E editor profile";

    // Typed in this browser's time zone; the page saves them as UTC.
    private static readonly string[] Dates = ["2026-03-04T05:06", "2026-03-05T05:06", "2026-03-06T07:08", "2026-03-07T07:08"];

    [E2EFact]
    public async Task Every_setting_is_sent_as_chosen_stored_and_shown_again_when_the_workflow_is_reopened()
    {
        using var folders = new TemporaryFolder();
        var watched = Directory.CreateDirectory(Path.Join(folders.Path, "watched")).FullName;
        var output = Directory.CreateDirectory(Path.Join(folders.Path, "output")).FullName;
        var work = Directory.CreateDirectory(Path.Join(folders.Path, "work")).FullName;
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var profileId = await WeirProfiles.CreateAsync(admin, ProfileName);
        var page = await NewPageAsync();
        await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);
        var log = RequestLog.Watch(page);
        long? id = null;
        try
        {
            var form = await WorkflowEditor.OpenNewAsync(page);
            await FillEverythingAsync(page, form, watched, output, work);

            var mark = log.Mark();
            await WorkflowEditor.Save(page).ClickAsync();
            await Expect(form).ToHaveCountAsync(0);
            id = await WorkflowEditor.IdOfAsync(admin, Name);

            var sent = Assert.Single(log.Since(mark));
            Assert.Equal($"POST {WeirClient.Api}/processing/libraries", $"{sent.Method} {sent.PathAndQuery}");
            var body = JsonNode.Parse(sent.Body!)!.AsObject();
            body.Remove("csrf_token");
            // The folder picker names the folder it chose with the separator after it.
            var chosen = watched + Path.DirectorySeparatorChar;
            var expected = await ChosenBodyAsync(page, profileId, chosen, output, work);
            Assert.True(JsonNode.DeepEquals(expected, body), $"The page sent {body.ToJsonString()}");

            var stored = await WorkflowEditor.ReadAsync(admin, id.Value);
            foreach (var (field, value) in expected.Where(field => !field.Key.EndsWith("_after", StringComparison.Ordinal) && !field.Key.EndsWith("_before", StringComparison.Ordinal)))
            {
                Assert.True(JsonNode.DeepEquals(value, stored[field]), $"{field}: the server holds {stored[field]?.ToJsonString()}, not {value?.ToJsonString()}");
            }

            foreach (var field in new[] { "created_after", "created_before", "modified_after", "modified_before" })
            {
                var held = (string)stored[field]!;
                Assert.True(
                    DateTimeOffset.Parse((string)expected[field]!, CultureInfo.InvariantCulture) == DateTimeOffset.Parse(held, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                    $"{field}: the server holds {held}, not {expected[field]}");
            }

            // Reopened, the editor shows what was saved.
            await page.ReloadAsync();
            await Expect(WorkflowEditor.Row(page, id.Value)).ToContainTextAsync(Name);
            var reopened = await WorkflowEditor.OpenEditAsync(page, id.Value);
            await AssertShownAsync(reopened, chosen, output, work);
        }
        finally
        {
            if (id is not null)
            {
                await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, id.Value);
            }

            await WeirProfiles.ClearAsync(admin, profileId);
        }
    }

    [E2EFact]
    public async Task Renaming_a_saved_workflow_sends_the_trimmed_name_in_a_PUT_and_the_list_shows_it()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);
        var id = await WeirWorkflowApi.CreateAsync(page.Context, BaseUrl, "E2E before rename", string.Empty, string.Empty);
        try
        {
            await page.ReloadAsync();
            var log = RequestLog.Watch(page);
            var form = await WorkflowEditor.OpenEditAsync(page, id);
            await WorkflowEditor.Text(form, "Name").FillAsync("  E2E after rename  ");
            var mark = log.Mark();
            await WorkflowEditor.Save(page).ClickAsync();
            await Expect(form).ToHaveCountAsync(0);

            var sent = Assert.Single(log.Since(mark));
            Assert.Equal($"PUT {WeirClient.Api}/processing/libraries/{id}", $"{sent.Method} {sent.PathAndQuery}");
            Assert.Equal("E2E after rename", (string?)JsonNode.Parse(sent.Body!)!["name"]);
            await Expect(WorkflowEditor.Row(page, id)).ToContainTextAsync("E2E after rename");
            Assert.Equal("E2E after rename", (string?)(await WorkflowEditor.ReadAsync(admin, id))["name"]);
        }
        finally
        {
            await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, id);
        }
    }

    [E2EFact]
    public async Task A_window_that_ends_before_it_starts_is_refused_with_the_reason_and_the_editor_stays_open()
    {
        using var admin = await AdminSession.OpenAsync(BaseUrl);
        var page = await NewPageAsync();
        await WorkflowEditor.OpenFilePathsAsync(page, BaseUrl);
        var id = await WeirWorkflowApi.CreateAsync(page.Context, BaseUrl, "E2E date window", string.Empty, string.Empty);
        try
        {
            await page.ReloadAsync();
            var form = await WorkflowEditor.OpenEditAsync(page, id);
            await WorkflowEditor.OpenGroupAsync(form, "Intake rules");
            await WorkflowEditor.Text(form, "Created after").FillAsync(Dates[1]);
            await WorkflowEditor.Text(form, "Created before").FillAsync(Dates[0]);

            await WorkflowEditor.Save(page).ClickAsync();
            var refusal = form.GetByTestId("processing-library-save-error");
            await Expect(refusal).ToBeVisibleAsync();
            await Expect(form).ToBeVisibleAsync();
            Assert.Null((await WorkflowEditor.ReadAsync(admin, id))["created_after"]);

            // A blank side is no limit: clearing both saves.
            await WorkflowEditor.Text(form, "Created after").FillAsync(string.Empty);
            await WorkflowEditor.Text(form, "Created before").FillAsync(string.Empty);
            await WorkflowEditor.Save(page).ClickAsync();
            await Expect(form).ToHaveCountAsync(0);
        }
        finally
        {
            await WeirWorkflowApi.DeleteAsync(page.Context, BaseUrl, id);
        }
    }

    private static async Task FillEverythingAsync(IPage page, ILocator form, string watched, string output, string work)
    {
        await WorkflowEditor.Text(form, "Name").FillAsync(Name);
        await WorkflowEditor.Choice(form, "Media type").SelectOptionAsync(new SelectOptionValue { Label = "TV episodes" });
        await WorkflowEditor.Choice(form, "Rules profile").SelectOptionAsync(new SelectOptionValue { Label = ProfileName });

        var writer = WorkflowEditor.Choice(form, "Writes files with");
        await Expect(form).ToContainTextAsync("mkvmerge writes MKV files when it is installed");
        await writer.SelectOptionAsync(new SelectOptionValue { Label = "FFmpeg only" });
        await Expect(form).ToContainTextAsync("FFmpeg writes every file, both new downloads and files already in your library.");
        await Expect(form).Not.ToContainTextAsync("mkvmerge writes MKV files when it is installed");

        await ChooseFolderAsync(page, form, "watched folder", watched);
        await WorkflowEditor.Text(form, "Output folder").FillAsync(output);
        await WorkflowEditor.Text(form, "Work folder").FillAsync(work);

        await WorkflowEditor.OpenGroupAsync(form, "Intake rules");
        await WorkflowEditor.Text(form, "File types").FillAsync(".mkv,.mp4");
        await WorkflowEditor.Text(form, "Downloader folders to ignore").FillAsync("tmp,partial");
        await WorkflowEditor.Text(form, "Path must match").FillAsync("*feature*");
        await WorkflowEditor.Text(form, "Path must not match").FillAsync("*sample*");
        await WorkflowEditor.Text(form, "Minimum file size (MB)").FillAsync("75");
        await WorkflowEditor.Text(form, "Maximum file size (MB)").FillAsync("9000");
        await WorkflowEditor.Text(form, "Created after").FillAsync(Dates[0]);
        await WorkflowEditor.Text(form, "Created before").FillAsync(Dates[1]);
        await WorkflowEditor.Text(form, "Modified after").FillAsync(Dates[2]);
        await WorkflowEditor.Text(form, "Modified before").FillAsync(Dates[3]);
        await WorkflowEditor.Choice(form, "When a file is rejected").SelectOptionAsync("delete_file");
        await WorkflowEditor.Check(form, "Skip hidden files").SetCheckedAsync(false);
        await WorkflowEditor.Check(form, "Only inspect the top folder").SetCheckedAsync(true);

        await WorkflowEditor.OpenGroupAsync(form, "File readiness");
        await WorkflowEditor.Text(form, "A new file is ready once it hasn't changed for (seconds)").FillAsync("90");
        await WorkflowEditor.Text(form, "Look for new files every (seconds)").FillAsync("120");
        await WorkflowEditor.Check(form, "Watch this folder for changes").SetCheckedAsync(false);
        await WorkflowEditor.Check(form, "Ignore size changes").SetCheckedAsync(true);
        await WorkflowEditor.Check(form, "Skip read and write tests").SetCheckedAsync(true);

        await WorkflowEditor.OpenGroupAsync(form, "Output safety");
        await WorkflowEditor.Text(form, "Files that travel with the video").FillAsync(".srt,.nfo");
        await WorkflowEditor.Choice(form, "Existing output").SelectOptionAsync("keep_both");
        await WorkflowEditor.Text(form, "Keep at least this many GB free on the drive this workflow writes to").FillAsync("2.5");
        await WorkflowEditor.Check(form, "Preserve original timestamps").SetCheckedAsync(true);
        await WorkflowEditor.Check(form, "New downloads: after cleaning, delete the original download").SetCheckedAsync(false);

        await WorkflowEditor.OpenGroupAsync(form, "Capacity");
        await WorkflowEditor.Text(form, "Most files at once from this workflow").FillAsync("1");
        await WorkflowEditor.Text(form, "Priority (higher goes first)").FillAsync("7");

        await WorkflowEditor.OpenGroupAsync(form, "When a file fails");
        await WorkflowEditor.Text(form, "Maximum automatic attempts").FillAsync("5");
        await WorkflowEditor.Text(form, "First retry delay (seconds)").FillAsync("120");
        await WorkflowEditor.Check(form, "Retry processing failures").SetCheckedAsync(false);
        await WorkflowEditor.Check(form, "Retry files that failed the pre-check").SetCheckedAsync(true);
        await WorkflowEditor.Choice(form, "When retries run out").SelectOptionAsync("hold");

        await WorkflowEditor.OpenGroupAsync(form, "Advanced");
        await WorkflowEditor.Choice(form, "FFmpeg compatibility").SelectOptionAsync("experimental");
    }

    /// <summary>Browse beside a folder opens the picker over the server's folders; Go to path then Use this folder puts the folder in the field.</summary>
    private static async Task ChooseFolderAsync(IPage page, ILocator form, string label, string folder)
    {
        await form.GetByTitle($"Choose the {label}").ClickAsync();
        var picker = page.GetByRole(AriaRole.Dialog, new() { Name = $"Choose the {label}", Exact = true });
        await Expect(picker).ToBeVisibleAsync();
        await picker.GetByLabel("Folder path").FillAsync(folder);
        await picker.GetByRole(AriaRole.Button, new() { Name = "Go to path" }).ClickAsync();
        await Expect(picker.Locator(".mm-folder-picker__current")).ToContainTextAsync(folder);
        await picker.GetByRole(AriaRole.Button, new() { Name = "Use this folder" }).ClickAsync();
        await Expect(picker).ToHaveCountAsync(0);
        // The picker names the folder it is in with the separator after it.
        await Expect(WorkflowEditor.Text(form, char.ToUpperInvariant(label[0]) + label[1..])).ToHaveValueAsync(folder + Path.DirectorySeparatorChar);
    }

    private static async Task<JsonObject> ChosenBodyAsync(IPage page, long profileId, string watched, string output, string work)
    {
        var body = WorkflowBodies.Default(Name);
        body["media_type"] = "tv";
        body["rule_set_id"] = profileId;
        body["remux_writer"] = "ffmpeg";
        body["watched_folder"] = watched;
        body["output_folder"] = output;
        body["work_folder"] = work;
        body["media_extensions_csv"] = ".mkv,.mp4";
        body["exclude_markers_csv"] = "tmp,partial";
        body["include_patterns_csv"] = "*feature*";
        body["exclude_patterns_csv"] = "*sample*";
        body["min_file_size_mb"] = 75;
        body["max_file_size_mb"] = 9000;
        string[] windows = ["created_after", "created_before", "modified_after", "modified_before"];
        for (var i = 0; i < windows.Length; i++)
        {
            body[windows[i]] = await page.EvaluateAsync<string>("typed => new Date(typed).toISOString()", Dates[i]);
        }

        body["rejected_file_action"] = "delete_file";
        body["exclude_hidden"] = false;
        body["top_level_only"] = true;
        body["ready_after_seconds"] = 90;
        body["scan_interval_seconds"] = 120;
        body["file_system_events_enabled"] = false;
        body["ignore_size_changes"] = true;
        body["skip_access_tests"] = true;
        body["sidecar_patterns_csv"] = ".srt,.nfo";
        body["output_collision_policy"] = "keep_both";
        body["minimum_free_disk_space_mb"] = 2560;
        body["preserve_original_timestamps"] = true;
        body["remove_original_after_success"] = false;
        body["max_concurrent_files"] = 1;
        body["priority"] = 7;
        body["max_attempts"] = 5;
        body["retry_backoff_seconds"] = 120;
        body["retry_execution_failures"] = false;
        body["retry_preflight_failures"] = true;
        body["failure_policy"] = "hold";
        body["ffmpeg_strictness"] = "experimental";
        return body;
    }

    private static async Task AssertShownAsync(ILocator form, string watched, string output, string work)
    {
        await Expect(WorkflowEditor.Text(form, "Name")).ToHaveValueAsync(Name);
        await Expect(WorkflowEditor.Choice(form, "Media type")).ToHaveValueAsync("tv");
        await Expect(WorkflowEditor.Choice(form, "Rules profile").Locator("option:checked")).ToHaveTextAsync(ProfileName);
        await Expect(WorkflowEditor.Choice(form, "Writes files with")).ToHaveValueAsync("ffmpeg");
        await Expect(WorkflowEditor.Text(form, "Watched folder")).ToHaveValueAsync(watched);
        await Expect(WorkflowEditor.Text(form, "Output folder")).ToHaveValueAsync(output);
        await Expect(WorkflowEditor.Text(form, "Work folder")).ToHaveValueAsync(work);

        await WorkflowEditor.OpenGroupAsync(form, "Intake rules");
        await Expect(WorkflowEditor.Text(form, "File types")).ToHaveValueAsync(".mkv,.mp4");
        await Expect(WorkflowEditor.Text(form, "Downloader folders to ignore")).ToHaveValueAsync("tmp,partial");
        await Expect(WorkflowEditor.Text(form, "Path must match")).ToHaveValueAsync("*feature*");
        await Expect(WorkflowEditor.Text(form, "Path must not match")).ToHaveValueAsync("*sample*");
        await Expect(WorkflowEditor.Text(form, "Minimum file size (MB)")).ToHaveValueAsync("75");
        await Expect(WorkflowEditor.Text(form, "Maximum file size (MB)")).ToHaveValueAsync("9000");
        await Expect(WorkflowEditor.Text(form, "Created after")).ToHaveValueAsync(Dates[0]);
        await Expect(WorkflowEditor.Text(form, "Created before")).ToHaveValueAsync(Dates[1]);
        await Expect(WorkflowEditor.Text(form, "Modified after")).ToHaveValueAsync(Dates[2]);
        await Expect(WorkflowEditor.Text(form, "Modified before")).ToHaveValueAsync(Dates[3]);
        await Expect(WorkflowEditor.Choice(form, "When a file is rejected")).ToHaveValueAsync("delete_file");
        await Expect(WorkflowEditor.Check(form, "Skip hidden files")).Not.ToBeCheckedAsync();
        await Expect(WorkflowEditor.Check(form, "Only inspect the top folder")).ToBeCheckedAsync();

        await WorkflowEditor.OpenGroupAsync(form, "File readiness");
        await Expect(WorkflowEditor.Text(form, "A new file is ready once it hasn't changed for (seconds)")).ToHaveValueAsync("90");
        await Expect(WorkflowEditor.Text(form, "Look for new files every (seconds)")).ToHaveValueAsync("120");
        await Expect(WorkflowEditor.Check(form, "Watch this folder for changes")).Not.ToBeCheckedAsync();
        await Expect(WorkflowEditor.Check(form, "Ignore size changes")).ToBeCheckedAsync();
        await Expect(WorkflowEditor.Check(form, "Skip read and write tests")).ToBeCheckedAsync();

        await WorkflowEditor.OpenGroupAsync(form, "Output safety");
        await Expect(WorkflowEditor.Text(form, "Files that travel with the video")).ToHaveValueAsync(".srt,.nfo");
        await Expect(WorkflowEditor.Choice(form, "Existing output")).ToHaveValueAsync("keep_both");
        await Expect(WorkflowEditor.Text(form, "Keep at least this many GB free on the drive this workflow writes to")).ToHaveValueAsync("2.5");
        await Expect(WorkflowEditor.Check(form, "Preserve original timestamps")).ToBeCheckedAsync();
        await Expect(WorkflowEditor.Check(form, "New downloads: after cleaning, delete the original download")).Not.ToBeCheckedAsync();

        await WorkflowEditor.OpenGroupAsync(form, "Capacity");
        await Expect(WorkflowEditor.Text(form, "Most files at once from this workflow")).ToHaveValueAsync("1");
        await Expect(WorkflowEditor.Text(form, "Priority (higher goes first)")).ToHaveValueAsync("7");

        await WorkflowEditor.OpenGroupAsync(form, "When a file fails");
        await Expect(WorkflowEditor.Text(form, "Maximum automatic attempts")).ToHaveValueAsync("5");
        await Expect(WorkflowEditor.Text(form, "First retry delay (seconds)")).ToHaveValueAsync("120");
        await Expect(WorkflowEditor.Check(form, "Retry processing failures")).Not.ToBeCheckedAsync();
        await Expect(WorkflowEditor.Check(form, "Retry files that failed the pre-check")).ToBeCheckedAsync();
        await Expect(WorkflowEditor.Choice(form, "When retries run out")).ToHaveValueAsync("hold");

        await WorkflowEditor.OpenGroupAsync(form, "Advanced");
        await Expect(WorkflowEditor.Choice(form, "FFmpeg compatibility")).ToHaveValueAsync("experimental");
    }
}
