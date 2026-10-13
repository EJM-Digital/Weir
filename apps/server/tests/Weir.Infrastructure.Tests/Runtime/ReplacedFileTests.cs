using System.Text;
using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>
/// A file that another process replaces by renaming a new one over it is refused for a moment, or missing for an instant. A reader
/// looks again before it says anything about the file. Time is not involved: the pause between looks is where a test changes the file.
/// </summary>
public sealed class ReplacedFileTests : IDisposable
{
    private readonly TempDirectory _home = new();

    public void Dispose() => _home.Dispose();

    private string FilePath => _home.Join("replaced.json");

    [Fact]
    public void A_file_is_read_with_the_time_it_was_written()
    {
        File.WriteAllText(FilePath, "hello");
        var written = new DateTime(2026, 10, 13, 6, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(FilePath, written);

        var reading = ReplacedFile.ReadText(FilePath);

        Assert.Equal(ReplacedFileState.Read, reading.State);
        Assert.Equal("hello", reading.Text);
        Assert.Equal(written, reading.WrittenUtc);
    }

    [Fact]
    public void A_file_that_is_refused_for_a_moment_is_read_when_it_comes_back()
    {
        File.WriteAllText(FilePath, "now");
        var held = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
        var looks = 0;

        var reading = ReplacedFile.ReadText(FilePath, pause: _ =>
        {
            looks++;
            if (looks == 2)
            {
                held.Dispose();
            }
        });

        Assert.Equal(ReplacedFileState.Read, reading.State);
        Assert.Equal("now", reading.Text);
        Assert.Equal(2, looks);
    }

    [Fact]
    public void A_file_that_stays_refused_is_busy_never_absent_or_empty()
    {
        File.WriteAllText(FilePath, "now");
        using var held = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
        var looks = 0;

        var reading = ReplacedFile.ReadText(FilePath, pause: _ => looks++);

        Assert.Equal(ReplacedFileState.Busy, reading.State);
        Assert.Equal(string.Empty, reading.Text);
        Assert.Equal(ReplacedFile.Attempts - 1, looks);
    }

    [Fact]
    public void A_name_that_is_not_there_is_absent_at_the_first_look_and_nothing_waits()
    {
        // Most of these files are not there most of the time (no update waiting, no request from the tray, nothing saved yet), and
        // a file is renamed over, never deleted and written again, so a missing name is the answer. Waiting for it would hold a
        // request thread for every read of a file that is simply not there.
        var reading = ReplacedFile.ReadText(FilePath, pause: _ => throw new InvalidOperationException("Nothing waits for a file that is not there."));

        Assert.Equal(ReplacedFileState.Absent, reading.State);
    }

    [Fact]
    public void A_caller_with_a_last_good_reading_asks_for_one_look_and_never_waits()
    {
        File.WriteAllText(FilePath, "now");
        using var held = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var reading = ReplacedFile.ReadText(FilePath, attempts: 1, pause: _ => throw new InvalidOperationException("One look does not wait."));

        Assert.Equal(ReplacedFileState.Busy, reading.State);
    }

    [Fact]
    public void A_folder_where_the_file_should_be_is_busy_not_absent()
    {
        Directory.CreateDirectory(FilePath);

        var reading = ReplacedFile.ReadText(FilePath, pause: _ => { });

        Assert.Equal(ReplacedFileState.Busy, reading.State);
    }

    [Fact]
    public void Bytes_the_encoding_refuses_are_a_damaged_file_not_a_busy_one()
    {
        File.WriteAllBytes(FilePath, [0x41, 0xC3, 0x28]);

        Assert.Throws<DecoderFallbackException>(() => ReplacedFile.ReadText(FilePath, new UTF8Encoding(false, throwOnInvalidBytes: true), pause: _ => { }));
    }

    [Fact]
    public async Task A_file_being_renamed_over_is_never_taken_for_damaged_by_a_reader_that_looks_again()
    {
        var path = FilePath;
        File.WriteAllText(path, "{\"n\":0}");
        var writing = true;
        var writer = Task.Run(() =>
        {
            for (var n = 1; n <= 1_000; n++)
            {
                try
                {
                    AtomicFileWriter.Replace(_home.Path, "replaced.json", Encoding.UTF8.GetBytes($"{{\"n\":{n}}}"));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Refused while a reader has it open: the next write replaces it.
                }
            }

            writing = false;
        });

        var read = 0;
        while (Volatile.Read(ref writing))
        {
            var reading = ReplacedFile.ReadText(path, pause: _ => { });
            if (reading.State == ReplacedFileState.Read)
            {
                Assert.StartsWith("{\"n\":", reading.Text, StringComparison.Ordinal);
                Assert.EndsWith("}", reading.Text, StringComparison.Ordinal);
                read++;
            }
        }

        await writer;
        Assert.True(read > 0);
    }
}
