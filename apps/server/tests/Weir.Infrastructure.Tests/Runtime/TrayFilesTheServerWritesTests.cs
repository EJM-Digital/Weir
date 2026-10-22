using System.Text.Json;
using System.Text.Json.Nodes;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>
/// The files the server writes for the Windows tray (<c>tray-status.json</c>, <c>work-state.json</c>, <c>update-backup-ready</c>,
/// <c>update-backup-result.json</c>, <c>startup-error.txt</c>, <c>startup-progress.json</c>) are exactly what the tray's own tests read
/// (<c>fixtures/tray-files</c>), so a key the server renames fails here, and the same recorded file fails the tray's reader if it cannot
/// read it.
/// </summary>
public sealed class TrayFilesTheServerWritesTests : IDisposable
{
    private static readonly DateTimeOffset WrittenAt = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly StoreFixture _store = new(("WEIR_RUNTIME", "windows"));

    public void Dispose() => _store.Dispose();

    private string Home => _store.Options.WeirHome;

    private static string Recorded(string name) => TrayFixtures.Text(name);

    private string Written(string name) => File.ReadAllText(Path.Join(Home, name));

    [Fact]
    public void The_status_the_tray_shows_is_written_as_recorded()
    {
        var status = new TrayStatus(
            Paused: true,
            PausedUntil: new DateTime(2026, 1, 15, 18, 30, 0, DateTimeKind.Utc),
            ManagersUnreachable: ["Deluno"],
            FoldersUnreachable: ["The watched folder for Movies"],
            ServerOk: true);

        Assert.Equal(Recorded("tray-status.json"), status.ToJson());
    }

    [Fact]
    public void The_answer_to_is_Weir_idle_is_written_as_recorded()
    {
        new UpdateFiles(_store.Options, _store.Clock).WriteWorkState(busy: false, WrittenAt);

        Assert.Equal(Recorded("work-state.json"), Written("work-state.json"));
    }

    [Fact]
    public void The_reason_a_start_failed_is_written_as_recorded()
    {
        StartupNotes.WriteError(Home, "Weir can't open its data", "Weir can't open its database because another program is using it. Close that program, then start Weir again.");

        Assert.Equal(Recorded("startup-error.txt"), Written(StartupNotes.ErrorFileName));
    }

    [Fact]
    public void How_far_a_start_has_got_is_written_as_recorded_and_sent_to_the_tray_on_standard_output_the_same_way()
    {
        var lines = new List<string>();
        var progress = new StartupProgress(Home, processId: 4242, _store.Clock, lines.Add);

        progress.Enter(StartupPhase.Migrating, step: 3, steps: 7, expectWithin: TimeSpan.FromSeconds(468));
        progress.Beat();

        Assert.Equal(Recorded("startup-progress.json"), Written(StartupProgress.FileName));
        Assert.Equal(StartupProgress.PipePrefix + Recorded("startup-progress.json"), lines[^1]);
    }

    [Fact]
    public async Task The_marker_that_says_the_server_can_save_a_copy_has_the_recorded_keys_for_this_process()
    {
        var watcher = new TrayUpdateBackupWatcher(_store.Options, _store.Database, _store.Clock, new CapturingLogger<TrayUpdateBackupWatcher>());
        await watcher.StartAsync(CancellationToken.None);

        var written = Written(TrayUpdateBackupWatcher.ReadyFileName);
        await watcher.StopAsync(CancellationToken.None);
        watcher.Dispose();

        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Assert.Equal(Shape(Recorded("update-backup-ready")), Shape(written));
        Assert.Equal(self.Id, (int)JsonNode.Parse(written)!["pid"]!);
        Assert.EndsWith("Z", (string)JsonNode.Parse(written)!["started_at"]!, StringComparison.Ordinal);
        Assert.EndsWith("Z", (string)JsonNode.Parse(Recorded("update-backup-ready"))!["started_at"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_answer_to_a_saved_copy_is_written_as_recorded()
    {
        var result = await AnswerAsync(copyCanBeSaved: true);

        Assert.Equal(Recorded("update-backup-result-saved.json"), WithTheRecordedPath(result, "update-backup-result-saved.json"));
    }

    [Fact]
    public async Task The_answer_to_a_copy_that_could_not_be_saved_is_written_as_recorded()
    {
        var result = await AnswerAsync(copyCanBeSaved: false);

        Assert.Equal(Recorded("update-backup-result-failed.json"), WithTheRecordedPath(result, "update-backup-result-failed.json"));
    }

    // The request is the one the tray recorded; the answer is what the server writes for it.
    private async Task<string> AnswerAsync(bool copyCanBeSaved)
    {
        if (!copyCanBeSaved)
        {
            Directory.CreateDirectory(_store.Options.BackupDir);
            await File.WriteAllTextAsync(PreUpdateBackup.FolderIn(_store.Options.BackupDir), "in the way");
        }

        var watcher = new TrayUpdateBackupWatcher(_store.Options, _store.Database, _store.Clock, new CapturingLogger<TrayUpdateBackupWatcher>());
        await watcher.StartAsync(CancellationToken.None);
        try
        {
            await File.WriteAllTextAsync(Path.Join(Home, TrayUpdateBackupWatcher.RequestFileName), Recorded("update-backup-request.json"));
            var resultPath = Path.Join(Home, TrayUpdateBackupWatcher.ResultFileName);
            await Eventually.ThatAsync(() => File.Exists(resultPath) && ((string?)JsonNode.Parse(File.ReadAllText(resultPath))?["state"]) is "saved" or "failed");
            return File.ReadAllText(resultPath);
        }
        finally
        {
            await watcher.StopAsync(CancellationToken.None);
            watcher.Dispose();
        }
    }

    // Where the copy went depends on the machine the test runs on, so the answer is put as the recorded file has it.
    private static string WithTheRecordedPath(string answer, string recordedName)
    {
        var path = (string?)JsonNode.Parse(answer)!["path"];
        var recordedPath = (string?)JsonNode.Parse(Recorded(recordedName))!["path"];
        return path is null ? answer : answer.Replace(JsonSerializer.Serialize(path), JsonSerializer.Serialize(recordedPath), StringComparison.Ordinal);
    }

    // The keys of a file and the kind of value each holds, in order.
    private static string Shape(string json) =>
        string.Join(',', JsonNode.Parse(json)!.AsObject().Select(property => $"{property.Key}:{property.Value?.GetValueKind()}"));
}
