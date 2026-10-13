using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>
/// startup-progress.json: what a starting server tells the tray about how far it has got. Time is fake, so nothing here waits for
/// a clock.
/// </summary>
public sealed class StartupProgressTests : IDisposable
{
    private const int ProcessId = 4242;
    private readonly TempDirectory _home = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 6, 0, 0, TimeSpan.Zero));

    public void Dispose() => _home.Dispose();

    private string FilePath => _home.Join(StartupProgress.FileName);

    // A refused write is tried again after a pause (StartupProgress.FileRetryAfter); these tests do not wait for it.
    private StartupProgress Progress() => new(_home.Path, ProcessId, _time) { Pause = _ => { } };

    private JsonElement Read() => JsonDocument.Parse(File.ReadAllText(FilePath)).RootElement;

    [Fact]
    public void A_phase_is_written_with_the_process_the_time_and_no_steps()
    {
        Progress().Enter(StartupPhase.OpeningDatabase);

        var file = Read();
        Assert.Equal(ProcessId, file.GetProperty("pid").GetInt32());
        Assert.Equal("opening_database", file.GetProperty("phase").GetString());
        Assert.Equal(0, file.GetProperty("step").GetInt32());
        Assert.Equal(0, file.GetProperty("steps").GetInt32());
        Assert.Equal(0, file.GetProperty("expect_within_s").GetInt64());
        Assert.Equal(_time.GetUtcNow(), file.GetProperty("at").GetDateTimeOffset());
    }

    [Fact]
    public void Migrating_says_which_step_of_how_many()
    {
        Progress().Enter(StartupPhase.Migrating, 3, 7);

        var file = Read();
        Assert.Equal("migrating", file.GetProperty("phase").GetString());
        Assert.Equal(3, file.GetProperty("step").GetInt32());
        Assert.Equal(7, file.GetProperty("steps").GetInt32());
    }

    [Theory]
    [InlineData(StartupPhase.Starting, "starting")]
    [InlineData(StartupPhase.OpeningDatabase, "opening_database")]
    [InlineData(StartupPhase.SavingCopy, "saving_copy")]
    [InlineData(StartupPhase.Migrating, "migrating")]
    [InlineData(StartupPhase.StartingListener, "starting_listener")]
    [InlineData(StartupPhase.Ready, "ready")]
    public void Every_phase_has_the_name_the_tray_reads(StartupPhase phase, string name)
    {
        Progress().Enter(phase);

        Assert.Equal(name, Read().GetProperty("phase").GetString());
    }

    [Fact]
    public void A_phase_can_ask_for_longer_silence_and_a_beat_keeps_asking()
    {
        var progress = Progress();
        progress.Enter(StartupPhase.SavingCopy, expectWithin: TimeSpan.FromSeconds(468.9));

        Assert.Equal(468, Read().GetProperty("expect_within_s").GetInt64());

        _time.Advance(StartupProgress.BeatEvery);
        progress.Beat();
        Assert.Equal(468, Read().GetProperty("expect_within_s").GetInt64());

        progress.Enter(StartupPhase.StartingListener);
        Assert.Equal(0, Read().GetProperty("expect_within_s").GetInt64());
    }

    [Fact]
    public void A_beat_inside_the_interval_does_not_write_and_one_after_it_does()
    {
        var progress = Progress();
        progress.Enter(StartupPhase.SavingCopy);
        var first = Read().GetProperty("at").GetDateTimeOffset();

        _time.Advance(StartupProgress.BeatEvery - TimeSpan.FromMilliseconds(1));
        progress.Beat();
        Assert.Equal(first, Read().GetProperty("at").GetDateTimeOffset());

        _time.Advance(TimeSpan.FromMilliseconds(1));
        progress.Beat();
        var second = Read().GetProperty("at").GetDateTimeOffset();
        Assert.Equal(first + StartupProgress.BeatEvery, second);
        Assert.Equal("saving_copy", Read().GetProperty("phase").GetString());
    }

    [Fact]
    public void A_beat_keeps_the_step_it_was_in()
    {
        var progress = Progress();
        progress.Enter(StartupPhase.Migrating, 2, 5);

        _time.Advance(StartupProgress.BeatEvery);
        progress.Beat();

        Assert.Equal(2, Read().GetProperty("step").GetInt32());
        Assert.Equal(5, Read().GetProperty("steps").GetInt32());
    }

    [Fact]
    public void A_file_that_cannot_be_written_never_stops_the_start()
    {
        // The data folder is a file, so nothing can be made inside it.
        var blocked = _home.Join("blocked");
        File.WriteAllText(blocked, "not a folder");
        var progress = new StartupProgress(blocked, ProcessId, _time) { Pause = _ => { } };

        progress.Enter(StartupPhase.Starting);
        _time.Advance(StartupProgress.BeatEvery);
        progress.Beat();

        Assert.True(File.Exists(blocked));
    }

    [Fact]
    public async Task A_reader_never_sees_a_half_written_file_while_the_phase_is_rewritten_over_and_over()
    {
        var progress = Progress();
        progress.Enter(StartupPhase.Migrating, 0, 2_000);
        var writing = true;
        var writer = Task.Run(() =>
        {
            for (var step = 1; step <= 2_000; step++)
            {
                progress.Enter(StartupPhase.Migrating, step, 2_000);
            }

            writing = false;
        });

        var reads = 0;
        while (Volatile.Read(ref writing))
        {
            string text;
            try
            {
                using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
            catch (Exception exception) when (exception is (IOException and not FileNotFoundException) or UnauthorizedAccessException)
            {
                // The rename was under way; a reader tries again, as the tray does.
                continue;
            }

            // Whole JSON with every field, never a prefix of it.
            var file = JsonDocument.Parse(text).RootElement;
            Assert.Equal("migrating", file.GetProperty("phase").GetString());
            Assert.Equal(ProcessId, file.GetProperty("pid").GetInt32());
            Assert.Equal(2_000, file.GetProperty("steps").GetInt32());
            reads++;
        }

        await writer;
        Assert.True(reads > 0);
        Assert.Empty(Directory.EnumerateFiles(_home.Path, ".*.tmp"));
    }
}
