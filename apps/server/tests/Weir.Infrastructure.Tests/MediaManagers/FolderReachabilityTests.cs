using Microsoft.Extensions.Logging;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// The tray's dot goes amber for a watched, work or output folder Weir cannot reach, so Weir looks at those folders itself,
/// browser or no browser, and says so on the folder-checks topic when its answer changes. A folder is reported after two
/// misses in a row, a share that hangs counts as a miss, and none of it holds up the server.
/// </summary>
public sealed class FolderReachabilityTests : IAsyncLifetime, IDisposable
{
    private sealed class Probe : IFolderProbe
    {
        private int _hungCalls;

        public HashSet<string> Existing { get; } = [];

        public HashSet<string> Unreadable { get; } = [];

        public bool Fails { get; set; }

        /// <summary>A folder whose question does not return until <see cref="Release"/> is set.</summary>
        public string? Hangs { get; set; }

        public ManualResetEventSlim Release { get; } = new();

        public int HungCalls => Volatile.Read(ref _hungCalls);

        public bool Exists(string path)
        {
            if (path == Hangs)
            {
                Interlocked.Increment(ref _hungCalls);
                Release.Wait();
            }

            return Fails ? throw new InvalidOperationException("The share did not answer.") : Existing.Contains(path);
        }

        public bool CanRead(string path) => !Unreadable.Contains(path);

        public bool CanWrite(string path) => true;

        public string? ResolveFinalPath(string path) => path;

        public bool? SameFilesystem(string first, string second) => true;
    }

    private const string Watched = "/media/movies/watched";
    private const string Work = "/media/movies/work";
    private const string Output = "/media/movies/output";

    private readonly StoreFixture _store = new();
    private readonly DataChangePublisher _changes = new();
    private readonly Probe _probe = new() { Existing = { Watched, Work, Output } };
    private readonly CapturingLogger<FolderReachability> _log = new();
    private readonly FolderReachability _reachability;

    public FolderReachabilityTests() =>
        _reachability = new FolderReachability(_store.Database, new LibraryStore(), _store.Options, _probe, _changes, _store.Clock, _log);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _reachability.StopAsync(CancellationToken.None);

    public void Dispose()
    {
        _probe.Release.Set();
        _reachability.Dispose();
        _store.Dispose();
    }

    private Task AddWorkflowAsync(string name = "Films", string watched = Watched, string work = Work, string output = Output, bool enabled = true) =>
        _store.Execute(
            "INSERT INTO libraries (name, media_type, enabled, watched_folder, work_folder, output_folder) " +
            $"VALUES ('{name}', 'movie', {(enabled ? 1 : 0)}, '{watched}', '{work}', '{output}')");

    private Task LookAsync() => _reachability.LookAsync(CancellationToken.None);

    /// <summary>A look at folders one of which hangs: time moves on until the look has given up waiting for it.</summary>
    private async Task LookAdvancingAsync()
    {
        var look = LookAsync();
        while (!look.IsCompleted)
        {
            _store.Clock.Advance(FolderReachability.ProbeTimeout);
            await Task.Delay(10);
        }

        await look;
    }

    private async Task<List<string>> TopicsAfterAsync(Func<Task> look)
    {
        using var heard = _changes.Subscribe();
        await look();
        heard.Dispose();
        var topics = new List<string>();
        await foreach (var topic in heard.ReadAllAsync(CancellationToken.None))
        {
            topics.Add(topic);
        }

        return topics;
    }

    [Fact]
    public async Task Folders_that_answer_leave_nothing_unreachable_and_say_nothing()
    {
        await AddWorkflowAsync();

        var topics = await TopicsAfterAsync(LookAsync);

        Assert.Empty(_reachability.Unreachable);
        Assert.Empty(topics);
        Assert.True(_reachability.FirstLook.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Each_folder_that_does_not_answer_is_named_in_plain_words_with_its_workflow_after_two_misses()
    {
        await AddWorkflowAsync("Films");
        await AddWorkflowAsync("Series", watched: "/media/shows/watched", work: "/media/shows/work", output: "/media/shows/output");
        _probe.Existing.Remove(Watched);
        _probe.Existing.UnionWith(["/media/shows/watched", "/media/shows/output"]);
        _probe.Unreadable.Add("/media/shows/output");

        await LookAsync();
        Assert.Empty(_reachability.Unreachable);
        await LookAsync();

        Assert.Equal(
            ["The watched folder for Films", "The work folder for Series", "The output folder for Series"],
            _reachability.Unreachable);
    }

    [Fact]
    public async Task One_miss_is_not_reported_and_the_first_look_that_finds_a_folder_drops_it()
    {
        await AddWorkflowAsync();
        _probe.Existing.Remove(Output);

        Assert.Empty(await TopicsAfterAsync(LookAsync));
        _probe.Existing.Add(Output);
        Assert.Empty(await TopicsAfterAsync(LookAsync));
        _probe.Existing.Remove(Output);
        Assert.Empty(await TopicsAfterAsync(LookAsync));
        Assert.Empty(_reachability.Unreachable);

        Assert.Equal([DataTopics.FolderChecks], await TopicsAfterAsync(LookAsync));
        Assert.Equal(["The output folder for Films"], _reachability.Unreachable);

        _probe.Existing.Add(Output);
        Assert.Equal([DataTopics.FolderChecks], await TopicsAfterAsync(LookAsync));
        Assert.Empty(_reachability.Unreachable);
    }

    [Fact]
    public async Task The_first_look_is_not_done_while_a_miss_waits_to_be_confirmed()
    {
        await AddWorkflowAsync();
        _probe.Existing.Remove(Watched);

        await LookAsync();
        Assert.False(_reachability.FirstLook.IsCompleted);

        await LookAsync();
        Assert.True(_reachability.FirstLook.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_workflow_that_is_off_a_folder_not_set_and_a_default_work_folder_not_made_yet_are_not_unreachable()
    {
        _probe.Existing.Clear();
        await AddWorkflowAsync("Switched off one", enabled: false);
        await AddWorkflowAsync("Half set up", watched: string.Empty, work: string.Empty, output: string.Empty);
        await AddWorkflowAsync("Default work folder", watched: Watched, work: string.Empty, output: Output);
        _probe.Existing.UnionWith([Watched, Output]);

        await LookAsync();
        await LookAsync();

        Assert.Empty(_reachability.Unreachable);
    }

    [Fact]
    public async Task A_look_that_fails_keeps_the_last_answer_and_still_ends_the_wait_for_the_first_look()
    {
        await AddWorkflowAsync();
        _probe.Fails = true;

        await LookAsync();

        Assert.True(_reachability.FirstLook.IsCompletedSuccessfully);
        Assert.True(_log.Logged(LogLevel.Warning, "could not check whether it can reach"));
        Assert.Empty(_reachability.Unreachable);

        _probe.Fails = false;
        _probe.Existing.Remove(Watched);
        await LookAsync();
        await LookAsync();
        _probe.Fails = true;
        await LookAsync();

        Assert.Equal(["The watched folder for Films"], _reachability.Unreachable);
    }

    [Fact]
    public async Task A_share_that_hangs_is_unreachable_and_is_not_asked_again_until_it_answers()
    {
        await AddWorkflowAsync();
        _probe.Hangs = Watched;

        await LookAdvancingAsync();
        Assert.Empty(_reachability.Unreachable);
        await LookAdvancingAsync();
        await LookAdvancingAsync();

        Assert.Equal(["The watched folder for Films"], _reachability.Unreachable);
        Assert.Equal(1, _probe.HungCalls);

        _probe.Release.Set();
        await Eventually.ThatAsync(async () =>
        {
            await LookAsync();
            return _reachability.Unreachable.Count == 0;
        });
    }

    [Fact]
    public async Task Stopping_does_not_wait_for_a_share_that_hangs()
    {
        await AddWorkflowAsync();
        _probe.Hangs = Watched;
        await _reachability.StartAsync(CancellationToken.None);
        await Eventually.ThatAsync(() => _probe.HungCalls == 1);

        // The probe is never released here, so a stop that waited for it would never return: the limit only ends such a wait as a
        // failure. How quickly a stop that does not wait returns depends on the machine, not on this code.
        await _reachability.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Running_it_looks_at_once_when_the_workflows_change_confirms_a_miss_soon_and_looks_again_after_the_interval()
    {
        await _reachability.StartAsync(CancellationToken.None);
        await _reachability.FirstLook;
        Assert.Empty(_reachability.Unreachable);

        _probe.Existing.Remove(Watched);
        await AddWorkflowAsync();
        _changes.Publish(DataTopics.Libraries);
        await Eventually.ThatAsync(() =>
        {
            _store.Clock.Advance(FolderReachability.ConfirmAfter);
            return _reachability.Unreachable.SequenceEqual(["The watched folder for Films"]);
        });

        _probe.Existing.Add(Watched);
        await Eventually.ThatAsync(() =>
        {
            _store.Clock.Advance(FolderReachability.Every);
            return _reachability.Unreachable.Count == 0;
        });
    }
}
