using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>
/// The two ways progress goes out: a line on standard output, which the tray judges a start by, and the file, for readers outside
/// the tray. Windows refuses a rename over a file that is open or being scanned (as access denied or a sharing violation), so
/// the file is retried and never held back the line; a file that cannot be written at all never stops the start.
/// </summary>
public sealed class StartupProgressChannelsTests : IDisposable
{
    private const int ProcessId = 4242;
    private readonly TempDirectory _home = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 6, 0, 0, TimeSpan.Zero));
    private readonly List<string> _pipe = [];
    private readonly List<TimeSpan> _pauses = [];

    public void Dispose() => _home.Dispose();

    private StartupProgress Progress(Action<string, string, byte[]>? replace = null, bool pipe = true)
    {
        var progress = new StartupProgress(_home.Path, ProcessId, _time, pipe ? _pipe.Add : null)
        {
            Pause = _pauses.Add,
        };
        if (replace is not null)
        {
            progress.Replace = replace;
        }

        return progress;
    }

    private string[] PipeLines() => [.. _pipe];

    [Fact]
    public void Every_record_is_a_line_on_the_pipe_with_the_prefix_and_the_same_json_as_the_file()
    {
        var progress = Progress();

        progress.Enter(StartupPhase.Migrating, 3, 7, TimeSpan.FromSeconds(468));
        progress.Beat();

        // The record after the first beat of the phase is the one that carries what the phase asked for.
        var line = PipeLines()[^1];
        Assert.Equal(2, PipeLines().Length);
        Assert.StartsWith(StartupProgress.PipePrefix, line, StringComparison.Ordinal);
        var json = line[StartupProgress.PipePrefix.Length..];
        Assert.Equal(File.ReadAllText(_home.Join(StartupProgress.FileName)), json);
        var record = JsonDocument.Parse(json).RootElement;
        Assert.Equal(ProcessId, record.GetProperty("pid").GetInt32());
        Assert.Equal("migrating", record.GetProperty("phase").GetString());
        Assert.Equal(468, record.GetProperty("expect_within_s").GetInt64());
    }

    [Fact]
    public void Without_the_tray_asking_nothing_goes_to_the_pipe()
    {
        var progress = Progress(pipe: false);

        progress.Enter(StartupPhase.Starting);

        Assert.Empty(PipeLines());
        Assert.True(File.Exists(_home.Join(StartupProgress.FileName)));
    }

    [Fact]
    public void A_beat_is_a_line_too_at_most_once_a_second()
    {
        var progress = Progress();
        progress.Enter(StartupPhase.SavingCopy);

        // The first beat of a phase is written at once; after it, one a second.
        progress.Beat();
        progress.Beat();
        _time.Advance(StartupProgress.BeatEvery);
        progress.Beat();
        progress.Beat();

        Assert.Equal(3, PipeLines().Length);
    }

    [Fact]
    public void A_rename_refused_as_access_denied_is_tried_again_and_succeeds()
    {
        var refused = 2;
        var progress = Progress((home, name, bytes) =>
        {
            if (refused-- > 0)
            {
                throw new UnauthorizedAccessException("Access to the path is denied.");
            }

            File.WriteAllBytes(Path.Join(home, name), bytes);
        });

        progress.Enter(StartupPhase.OpeningDatabase);

        Assert.Equal("opening_database", JsonDocument.Parse(File.ReadAllText(_home.Join(StartupProgress.FileName))).RootElement.GetProperty("phase").GetString());
        Assert.Equal([StartupProgress.FileRetryAfter, StartupProgress.FileRetryAfter], _pauses);
    }

    [Fact]
    public void A_rename_refused_as_a_sharing_violation_is_tried_again_and_succeeds()
    {
        var refused = 1;
        var progress = Progress((home, name, bytes) =>
        {
            if (refused-- > 0)
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }

            File.WriteAllBytes(Path.Join(home, name), bytes);
        });

        progress.Enter(StartupPhase.OpeningDatabase);

        Assert.True(File.Exists(_home.Join(StartupProgress.FileName)));
        Assert.Single(_pauses);
    }

    [Fact]
    public void A_file_that_stays_refused_is_given_up_on_after_the_attempts_and_the_pipe_has_said_it_all_the_same()
    {
        var attempts = 0;
        var progress = Progress((_, _, _) =>
        {
            attempts++;
            throw new UnauthorizedAccessException("Access to the path is denied.");
        });

        progress.Enter(StartupPhase.SavingCopy);

        Assert.Equal(StartupProgress.FileAttempts, attempts);
        Assert.Equal(StartupProgress.FileAttempts - 1, _pauses.Count);
        Assert.False(File.Exists(_home.Join(StartupProgress.FileName)));
        var line = Assert.Single(PipeLines());
        Assert.Contains("\"phase\":\"saving_copy\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_stayed_refused_is_written_by_the_next_beat_once_it_can_be()
    {
        var refusing = true;
        var progress = Progress((home, name, bytes) =>
        {
            if (refusing)
            {
                throw new UnauthorizedAccessException("Access to the path is denied.");
            }

            File.WriteAllBytes(Path.Join(home, name), bytes);
        });
        progress.Enter(StartupPhase.SavingCopy);
        Assert.False(File.Exists(_home.Join(StartupProgress.FileName)));

        refusing = false;
        _time.Advance(StartupProgress.BeatEvery);
        progress.Beat();

        Assert.Contains("saving_copy", File.ReadAllText(_home.Join(StartupProgress.FileName)), StringComparison.Ordinal);
    }
}
