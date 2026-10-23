using Weir.Core.Processing;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Jobs;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>The workflow store on payloads nobody wrote well, on limits that change, and on a row another writer removes mid-save.</summary>
public sealed class LibraryStoreEdgeTests : IDisposable
{
    private const string Remux = "processing.file.remux_pass.v1";
    private static readonly LibraryStore Store = new();

    private readonly JobsTestDatabase _db = new(keepSeedRows: true);

    public void Dispose() => _db.Dispose();

    private static ProcessingLibraryInput Input(string name, long maxConcurrentFiles = 0) =>
        new() { Name = name, MediaType = "movie", MaxConcurrentFiles = maxConcurrentFiles };

    private void Queue(string key, string? payload, string status = "pending") =>
        _db.Execute(
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status) VALUES (@key, @kind, @payload, @status)",
            ("@key", key), ("@kind", Remux), ("@payload", payload), ("@status", status));

    private async Task<T> InWork<T>(Func<UnitOfWork, Task<T>> work)
    {
        await using var uow = await UnitOfWork.OpenAsync(_db.Database);
        var result = await work(uow);
        await uow.CommitAsync();
        return result;
    }

    private Task<ProcessingLibraryRecord> Seeded(string mediaType) =>
        InWork(async uow => (await Store.ListAsync(uow)).First(row => row.MediaType == mediaType));

    [Fact]
    public async Task Queued_work_is_counted_for_the_workflow_it_names_and_payloads_that_name_nothing_Weir_can_read_are_skipped()
    {
        Queue("none", null);
        Queue("json5", "{media_scope:'movie'}");
        Queue("list", "[1]");
        Queue("no-scope", "{}");
        Queue("movie", """{"media_scope":"movie"}""");
        Queue("tv", """{"media_scope":"tv"}""");
        Queue("scope-not-text", """{"media_scope":5}""");
        Queue("other-library", """{"library_id":9999,"media_scope":"movie"}""");
        Queue("done", """{"media_scope":"movie"}""", status: "completed");
        var movies = await Seeded("movie");
        var tv = await Seeded("tv");

        Assert.Equal(3, await InWork(uow => Store.ActiveJobCountAsync(uow, movies)));
        Assert.Equal(1, await InWork(uow => Store.ActiveJobCountAsync(uow, tv)));
    }

    [Fact]
    public async Task Work_with_no_library_in_its_payload_is_not_counted_against_a_workflow_that_is_not_the_first_of_its_kind()
    {
        Queue("no-library", """{"media_scope":"movie"}""");
        var second = await InWork(uow => Store.CreateAsync(uow, Input("Anime")));

        Assert.Equal(0, await InWork(uow => Store.ActiveJobCountAsync(uow, second)));
    }

    [Fact]
    public async Task Work_for_a_kind_with_no_workflow_left_is_counted_against_no_one()
    {
        var tv = await Seeded("tv");
        _db.Execute("DELETE FROM libraries WHERE media_type = 'tv'");
        Queue("tv", """{"media_scope":"tv"}""");

        Assert.Equal(0, await InWork(uow => Store.ActiveJobCountAsync(uow, tv)));
    }

    [Theory]
    [InlineData(1, "still has 1 job queued or running")]
    [InlineData(2, "still has 2 jobs queued or running")]
    public async Task A_workflow_with_work_queued_cannot_be_removed_and_is_told_how_much(int jobs, string expected)
    {
        var movies = await Seeded("movie");
        foreach (var number in Enumerable.Range(1, jobs))
        {
            Queue($"job-{number}", $$"""{"library_id":{{movies.Id}}}""");
        }

        var refusal = await Assert.ThrowsAsync<ProcessingLibraryException>(async () =>
        {
            await using var uow = await UnitOfWork.OpenAsync(_db.Database);
            await Store.DeleteAsync(uow, movies);
        });

        Assert.Contains(expected, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_workflow_made_from_a_manager_library_with_no_connection_is_made_unlinked()
    {
        var created = await InWork(uow => Store.CreateDiscoveredAsync(uow, new ProcessingLibraryRecord { Name = "Anime", MediaType = "movie" }));

        Assert.Empty(await InWork(uow => Store.ManagerConnectionIdsAsync(uow, created.Id)));
    }

    [Fact]
    public async Task A_folder_the_manager_does_not_give_is_left_as_it_was()
    {
        var movies = await InWork(uow => Store.CreateAsync(uow, Input("Anime") with { WatchedFolder = "/in", OutputFolder = "/out" }));

        var kept = await InWork(uow => Store.SetFoldersFromManagerAsync(uow, movies, watchedFolder: null, outputFolder: null));

        Assert.Equal(("/in", "/out"), (kept.WatchedFolder, kept.OutputFolder));
    }

    [Fact]
    public async Task A_default_profile_removed_as_it_is_made_is_a_failure_not_a_null()
    {
        _db.Execute("UPDATE libraries SET rule_set_id = NULL");
        _db.Execute("CREATE TRIGGER vanish AFTER INSERT ON rule_sets BEGIN DELETE FROM rule_sets WHERE id = NEW.id; END");

        await Assert.ThrowsAsync<InvalidOperationException>(() => InWork(uow => Store.DefaultProfileIdAsync(uow, "movie")));
    }

    [Fact]
    public async Task A_workflow_may_change_how_many_files_it_runs_only_up_to_the_total()
    {
        var movies = await Seeded("movie");

        var raised = await InWork(uow => Store.UpdateAsync(uow, movies, Input(movies.Name, maxConcurrentFiles: 1)));
        var refused = await Assert.ThrowsAsync<ProcessingLibraryException>(() => InWork(uow => Store.UpdateAsync(uow, raised, Input(movies.Name, maxConcurrentFiles: 9))));

        Assert.Equal(1, raised.MaxConcurrentFiles);
        Assert.Contains("cannot be more than the", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_workflow_that_names_a_rule_set_that_is_gone_is_refused()
    {
        var refusal = await Assert.ThrowsAsync<ProcessingLibraryException>(() => InWork(uow => Store.CreateAsync(uow, Input("Anime") with { RuleSetId = 987654 })));

        Assert.Equal("No rule set with id 987654.", refusal.Message);
    }

    [Fact]
    public async Task The_default_profile_is_made_once_and_found_again_by_name()
    {
        _db.Execute("UPDATE libraries SET rule_set_id = NULL");

        var first = await InWork(uow => Store.DefaultProfileIdAsync(uow, "movie"));
        var again = await InWork(uow => Store.DefaultProfileIdAsync(uow, "movie"));

        Assert.Equal(first, again);
        Assert.Equal(1, _db.Count("SELECT count(*) FROM rule_sets WHERE name = 'Movies default'"));
    }

    [Fact]
    public async Task A_rule_set_name_that_is_taken_is_refused_on_create_and_on_rename()
    {
        var first = await InWork(uow => Store.CreateRuleSetAsync(uow, new LibraryRules.RuleSetInput { Name = "Kids" }));
        var second = await InWork(uow => Store.CreateRuleSetAsync(uow, new LibraryRules.RuleSetInput { Name = "Adults" }));

        await Assert.ThrowsAsync<ProcessingLibraryException>(() => InWork(uow => Store.CreateRuleSetAsync(uow, new LibraryRules.RuleSetInput { Name = " Kids " })));
        await Assert.ThrowsAsync<ProcessingLibraryException>(() => InWork(uow => Store.UpdateRuleSetAsync(uow, second, new LibraryRules.RuleSetInput { Name = "Kids" })));
        var kept = await InWork(uow => Store.UpdateRuleSetAsync(uow, first, new LibraryRules.RuleSetInput { Name = "Kids" }));

        Assert.Equal("Kids", kept.Name);
    }

    [Fact]
    public async Task Every_yes_or_no_of_a_rule_set_is_kept_as_it_was_saved_whichever_way_it_was_set()
    {
        var allOn = new LibraryRules.RuleSetInput
        {
            Name = "All on",
            RemoveCommentary = true, PreserveForcedSubs = true, PreserveDefaultSubs = true, KeepOriginalLanguage = true,
            OriginalLanguageKeepOnlyFirst = true, OriginalLanguageFirstIfNone = true, OriginalLanguageTreatEmptyAsOriginal = true,
            RemoveImages = true, RemoveAttachments = true, RemoveTitle = true, RemoveLanguageTags = true, RemoveOtherMetadata = true,
            RemoveHearingImpairedSubs = true, StandardizeTrackNames = true, ClearVideoTrackNames = true, RemoveChapters = true,
        };
        var allOff = allOn with
        {
            Name = "All off",
            RemoveCommentary = false, PreserveForcedSubs = false, PreserveDefaultSubs = false, KeepOriginalLanguage = false,
            OriginalLanguageKeepOnlyFirst = false, OriginalLanguageFirstIfNone = false, OriginalLanguageTreatEmptyAsOriginal = false,
            RemoveImages = false, RemoveAttachments = false, RemoveTitle = false, RemoveLanguageTags = false, RemoveOtherMetadata = false,
            RemoveHearingImpairedSubs = false, StandardizeTrackNames = false, ClearVideoTrackNames = false, RemoveChapters = false,
        };

        var on = await InWork(uow => Store.CreateRuleSetAsync(uow, allOn));
        var off = await InWork(uow => Store.UpdateRuleSetAsync(uow, on, allOff));
        var readBack = await InWork(uow => Store.GetRuleSetAsync(uow, off.Id));

        Assert.True(on.RemoveChapters && on.RemoveCommentary && on.PreserveDefaultSubs && on.OriginalLanguageTreatEmptyAsOriginal && on.StandardizeTrackNames);
        Assert.False(readBack!.RemoveChapters || readBack.RemoveCommentary || readBack.PreserveDefaultSubs || readBack.OriginalLanguageTreatEmptyAsOriginal || readBack.StandardizeTrackNames);
    }

    [Fact]
    public async Task A_rule_set_used_by_several_workflows_is_refused_with_how_many()
    {
        var shared = await InWork(uow => Store.CreateRuleSetAsync(uow, new LibraryRules.RuleSetInput { Name = "Shared" }));
        _db.Execute("UPDATE libraries SET rule_set_id = @id", ("@id", shared.Id));

        var refusal = await Assert.ThrowsAsync<ProcessingLibraryException>(async () =>
        {
            await using var uow = await UnitOfWork.OpenAsync(_db.Database);
            await Store.DeleteRuleSetAsync(uow, shared);
        });

        Assert.StartsWith("Shared is still used by 2 workflows.", refusal.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> Writes => new()
    {
        "create",
        "update",
        "create-discovered",
        "unlink",
        "adopt",
        "set-folders",
        "create-rule-set",
        "update-rule-set",
    };

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_row_removed_by_another_writer_between_the_save_and_the_read_back_is_a_failure_not_a_null(string write)
    {
        var movies = await Seeded("movie");
        var ruleSet = await InWork(uow => Store.CreateRuleSetAsync(uow, new LibraryRules.RuleSetInput { Name = "Temporary" }));
        var connectionId = Convert.ToInt64(_db.Scalar("INSERT INTO media_manager_connections (kind, name) VALUES ('deluno', 'Deluno') RETURNING id"), System.Globalization.CultureInfo.InvariantCulture);
        _db.Execute(write switch
        {
            "create" or "create-discovered" => "CREATE TRIGGER vanish AFTER INSERT ON libraries BEGIN DELETE FROM libraries WHERE id = NEW.id; END",
            "adopt" => "CREATE TRIGGER vanish AFTER INSERT ON library_manager_links BEGIN DELETE FROM libraries WHERE id = NEW.library_id; END",
            "create-rule-set" => "CREATE TRIGGER vanish AFTER INSERT ON rule_sets BEGIN DELETE FROM rule_sets WHERE id = NEW.id; END",
            "update-rule-set" => "CREATE TRIGGER vanish AFTER UPDATE ON rule_sets BEGIN DELETE FROM rule_sets WHERE id = NEW.id; END",
            _ => "CREATE TRIGGER vanish AFTER UPDATE ON libraries BEGIN DELETE FROM libraries WHERE id = NEW.id; END",
        });

        var failure = await Record.ExceptionAsync(async () =>
        {
            await using var uow = await UnitOfWork.OpenAsync(_db.Database);
            await Perform(write, uow, movies, ruleSet, connectionId);
        });

        Assert.IsType<InvalidOperationException>(failure);
    }

    private static async Task Perform(string write, UnitOfWork uow, ProcessingLibraryRecord movies, ProcessingRuleSetRecord ruleSet, long connectionId)
    {
        switch (write)
        {
            case "create":
                await Store.CreateAsync(uow, Input("Anime"));
                break;
            case "update":
                await Store.UpdateAsync(uow, movies, Input(movies.Name));
                break;
            case "create-discovered":
                await Store.CreateDiscoveredAsync(uow, new ProcessingLibraryRecord { Name = "Anime", MediaType = "movie" });
                break;
            case "unlink":
                await Store.UnlinkAsync(uow, movies);
                break;
            case "adopt":
                await Store.AdoptForManagerAsync(uow, movies, "Films", connectionId, "lib-1", watchedFolder: null, outputFolder: null);
                break;
            case "set-folders":
                await Store.SetFoldersFromManagerAsync(uow, movies, "/in", "/out");
                break;
            case "create-rule-set":
                await Store.CreateRuleSetAsync(uow, new LibraryRules.RuleSetInput { Name = "Gone" });
                break;
            default:
                await Store.UpdateRuleSetAsync(uow, ruleSet, new LibraryRules.RuleSetInput { Name = "Renamed" });
                break;
        }
    }
}
