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
    public void A_name_that_is_missing_for_an_instant_is_read_when_it_is_back()
    {
        var looks = 0;

        var reading = ReplacedFile.ReadText(FilePath, pause: _ =>
        {
            looks++;
            if (looks == 3)
            {
                File.WriteAllText(FilePath, "back");
            }
        });

        Assert.Equal(ReplacedFileState.Read, reading.State);
        Assert.Equal("back", reading.Text);
    }

    [Fact]
    public void A_name_that_is_missing_on_every_look_is_absent()
    {
        var looks = 0;

        var reading = ReplacedFile.ReadText(FilePath, pause: _ => looks++);

        Assert.Equal(ReplacedFileState.Absent, reading.State);
        Assert.Equal(ReplacedFile.Attempts - 1, looks);
    }

    [Fact]
    public void A_file_that_is_only_ever_deleted_first_is_not_looked_for_again()
    {
        var looks = 0;

        var reading = ReplacedFile.ReadText(FilePath, absentIsFinal: true, pause: _ => looks++);

        Assert.Equal(ReplacedFileState.Absent, reading.State);
        Assert.Equal(0, looks);
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
