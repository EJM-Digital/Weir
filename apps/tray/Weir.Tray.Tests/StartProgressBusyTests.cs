using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// While the server renames a new startup-progress.json over the old one, or a virus scanner has it, Windows refuses a reader as
/// access denied or as a sharing violation. That is the file being busy: it is read again after a moment, and when it stays busy
/// the look has no news. It is never taken for a missing file or a damaged one. The same record also arrives as a line on the
/// server's standard output, which cannot be busy; the newer of the two counts.
/// </summary>
public sealed class StartProgressBusyTests : IDisposable
{
    private const string Starting = """{"pid":7,"phase":"starting","at":"2026-10-13T06:22:58Z"}""";

    private readonly TempDirectory _home = TempDirectory.Create();

    public void Dispose() => _home.Dispose();

    private string FilePath => Path.Combine(_home.Path, StartProgressFile.FileName);

    [Fact]
    public void A_file_refused_as_access_denied_is_opened_again_and_read()
    {
        var refusals = 2;
        var pauses = new List<TimeSpan>();

        var reading = StartProgressFile.Read(
            _home.Path,
            _ => refusals-- > 0 ? throw new UnauthorizedAccessException("Access to the path is denied.") : Stream(Starting),
            pauses.Add);

        Assert.Equal(7, reading?.ProcessId);
        Assert.Equal([StartProgressFile.RetryAfter, StartProgressFile.RetryAfter], pauses);
    }

    [Fact]
    public void A_file_refused_as_a_sharing_violation_is_opened_again_and_read()
    {
        var refusals = 1;
        var pauses = new List<TimeSpan>();

        var reading = StartProgressFile.Read(
            _home.Path,
            _ => refusals-- > 0 ? throw new IOException("The process cannot access the file because it is being used by another process.") : Stream(Starting),
            pauses.Add);

        Assert.Equal(7, reading?.ProcessId);
        Assert.Single(pauses);
    }

    [Fact]
    public void A_file_that_stays_busy_is_no_news_after_the_attempts_and_not_an_error()
    {
        var opened = 0;
        var pauses = new List<TimeSpan>();

        var reading = StartProgressFile.Read(
            _home.Path,
            _ =>
            {
                opened++;
                throw new UnauthorizedAccessException("Access to the path is denied.");
            },
            pauses.Add);

        Assert.Null(reading);
        Assert.Equal(StartProgressFile.Attempts, opened);
        Assert.Equal(StartProgressFile.Attempts - 1, pauses.Count);
    }

    [Theory]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(DirectoryNotFoundException))]
    public void A_file_that_is_not_there_is_missing_at_once_not_busy(Type missing)
    {
        var opened = 0;

        var reading = StartProgressFile.Read(
            _home.Path,
            _ =>
            {
                opened++;
                throw (Exception)Activator.CreateInstance(missing, "not there")!;
            },
            _ => Assert.Fail("A missing file was waited for"));

        Assert.Null(reading);
        Assert.Equal(1, opened);
    }

    [Fact]
    public void A_file_really_held_by_another_reader_is_read_once_it_is_let_go()
    {
        File.WriteAllText(FilePath, Starting);
        var held = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Null(StartProgressFile.Read(_home.Path));

        held.Dispose();
        Assert.Equal(7, StartProgressFile.Read(_home.Path)?.ProcessId);
    }

    [Fact]
    public void A_progress_line_from_the_server_is_a_record_and_any_other_line_is_not()
    {
        var line = StartProgressFile.Prefix + """{"pid":5764,"phase":"migrating","step":3,"steps":7,"expect_within_s":468,"at":"2026-10-13T06:22:58Z"}""";

        Assert.Equal("migrating", StartProgressFile.ParseLine(line)?.Phase);
        Assert.Null(StartProgressFile.ParseLine("ready"));
        Assert.Null(StartProgressFile.ParseLine("2026-10-13 06:22:58,123 INFO Weir.Host.Startup Opening the database"));
        Assert.Null(StartProgressFile.ParseLine(StartProgressFile.Prefix + """{"pid":5764,"phase":"migra"""));
        Assert.Null(StartProgressFile.ParseLine(StartProgressFile.Prefix));
    }

    [Fact]
    public void The_newer_of_the_pipe_and_the_file_is_the_later_one_and_the_pipes_when_they_are_equal()
    {
        var early = new StartProgress(1, "opening_database", 0, 0, TimeSpan.Zero, new DateTimeOffset(2026, 10, 13, 6, 0, 1, TimeSpan.Zero));
        var late = early with { Phase = "saving_copy", At = early.At + TimeSpan.FromSeconds(5) };
        var sameTime = early with { Phase = "migrating" };

        Assert.Equal(late, StartProgressFile.Newer(early, late));
        Assert.Equal(late, StartProgressFile.Newer(late, early));
        Assert.Equal(early, StartProgressFile.Newer(early, sameTime));
        Assert.Equal(early, StartProgressFile.Newer(early, null));
        Assert.Equal(late, StartProgressFile.Newer(null, late));
        Assert.Null(StartProgressFile.Newer(null, null));
    }

    [Fact]
    public void The_merge_gives_the_same_record_every_time_so_readings_that_do_not_advance_never_look_like_progress()
    {
        var fromPipe = new StartProgress(1, "saving_copy", 0, 0, TimeSpan.Zero, new DateTimeOffset(2026, 10, 13, 6, 0, 5, TimeSpan.Zero));
        var fromFile = fromPipe with { Phase = "opening_database", At = fromPipe.At - TimeSpan.FromSeconds(4) };
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var watch = new StartWatch(StartPolicy.Default, time, 1, fromPipe.At - TimeSpan.FromSeconds(10));

        watch.Check(StartProgressFile.Newer(fromPipe, fromFile));
        time.Advance(StartPolicy.Default.StallWindow);

        // The file lags behind the pipe, and each look takes both again: the watch sees one record, unchanged, and calls it hung.
        Assert.True(watch.Check(StartProgressFile.Newer(fromPipe, fromFile)).Hung);
    }

    [Fact]
    public void The_feed_keeps_every_progress_line_of_its_server_and_words_only_a_new_phase_or_step()
    {
        var feed = new StartProgressFeed(5764);

        var first = feed.Accept(Line("migrating", 1, 1));
        var beat = feed.Accept(Line("migrating", 1, 2));
        var next = feed.Accept(Line("migrating", 2, 3));

        Assert.NotNull(first);
        Assert.Null(beat);
        Assert.Equal(2, next?.Step);
        Assert.Equal(3, feed.Latest?.At.Second);
    }

    [Fact]
    public void The_feed_ignores_another_process_a_broken_line_and_any_other_output()
    {
        var feed = new StartProgressFeed(5764);

        Assert.Null(feed.Accept(StartProgressFile.Prefix + """{"pid":1,"phase":"ready","at":"2026-10-13T06:00:00Z"}"""));
        Assert.Null(feed.Accept(StartProgressFile.Prefix + "{"));
        Assert.Null(feed.Accept("ready"));
        Assert.Null(feed.Latest);
    }

    private static string Line(string phase, int step, int second) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $$"""{{StartProgressFile.Prefix}}{"pid":5764,"phase":"{{phase}}","step":{{step}},"steps":7,"expect_within_s":0,"at":"2026-10-13T06:00:{{second:00}}Z"}""");

    private static MemoryStream Stream(string text) => new(System.Text.Encoding.UTF8.GetBytes(text));
}
