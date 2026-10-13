using System.Text.Json;

using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// Reading startup-progress.json: a missing, empty, torn or incomplete file is no reading, never an error and never half of one;
/// a complete file gives every field; and a file replaced over and over while it is read is always read whole.
/// </summary>
public sealed class StartProgressFileTests : IDisposable
{
    private readonly TempDirectory _home = TempDirectory.Create();

    public void Dispose() => _home.Dispose();

    private string FilePath => Path.Combine(_home.Path, StartProgressFile.FileName);

    private StartProgress? Read() => StartProgressFile.Read(_home.Path);

    [Fact]
    public void No_file_is_no_reading()
    {
        Assert.Null(Read());
    }

    [Fact]
    public void A_complete_file_gives_every_field()
    {
        File.WriteAllText(FilePath, """{"pid":5764,"phase":"migrating","step":3,"steps":7,"expect_within_s":468,"at":"2026-10-13T06:22:58.1234567Z"}""");

        var reading = Read();

        Assert.Equal(new StartProgress(5764, "migrating", 3, 7, TimeSpan.FromSeconds(468), DateTimeOffset.Parse("2026-10-13T06:22:58.1234567Z", System.Globalization.CultureInfo.InvariantCulture)), reading);
    }

    [Fact]
    public void A_file_without_the_optional_fields_reads_as_a_phase_with_no_steps_and_no_request()
    {
        File.WriteAllText(FilePath, """{"pid":1,"phase":"starting","at":"2026-10-13T06:22:58Z"}""");

        var reading = Read();

        Assert.NotNull(reading);
        Assert.Equal(0, reading.Step);
        Assert.Equal(0, reading.Steps);
        Assert.Equal(TimeSpan.Zero, reading.ExpectWithin);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("""{"pid":5764,"phase":"migra""")]
    [InlineData("""{"pid":5764,"phase":"migrating","step":3,"steps":7,"expect_within_s":468,"at":"2026-10-13T06:2""")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"phase":"starting","at":"2026-10-13T06:22:58Z"}""")]
    [InlineData("""{"pid":1,"at":"2026-10-13T06:22:58Z"}""")]
    [InlineData("""{"pid":1,"phase":"","at":"2026-10-13T06:22:58Z"}""")]
    [InlineData("""{"pid":1,"phase":"starting"}""")]
    [InlineData("""{"pid":"one","phase":"starting","at":"2026-10-13T06:22:58Z"}""")]
    public void A_file_that_is_empty_torn_or_missing_what_makes_a_reading_is_no_reading(string text)
    {
        File.WriteAllText(FilePath, text);

        Assert.Null(Read());
    }

    [Fact]
    public void A_negative_number_is_read_as_none()
    {
        File.WriteAllText(FilePath, """{"pid":1,"phase":"migrating","step":-4,"steps":-1,"expect_within_s":-60,"at":"2026-10-13T06:22:58Z"}""");

        var reading = Read();

        Assert.NotNull(reading);
        Assert.Equal(0, reading.Step);
        Assert.Equal(TimeSpan.Zero, reading.ExpectWithin);
    }

    // Windows refuses a rename over a file that a reader has open (access denied or a sharing violation), so the writer tries a
    // write again when it is refused, as the server's does, and the reading loop ends when the writer's task does, however it ended.
    [Fact(Timeout = 60_000)]
    public async Task A_file_replaced_over_and_over_while_it_is_read_is_always_read_whole_or_not_at_all()
    {
        AtomicFile.WriteAllText(_home.Path, StartProgressFile.FileName, Record(0));
        var writer = Task.Run(() =>
        {
            for (var step = 1; step <= 1_500; step++)
            {
                WriteAgainWhenRefused(Record(step));
            }
        });

        var whole = 0;
        var empty = 0;
        while (!writer.IsCompleted)
        {
            if (Read() is { } reading)
            {
                // Every field of one record, never a prefix of it or a mix of two.
                Assert.Equal(5764, reading.ProcessId);
                Assert.Equal("migrating", reading.Phase);
                Assert.Equal(2_000, reading.Steps);
                Assert.Equal(TimeSpan.FromSeconds(468), reading.ExpectWithin);
                whole++;
            }
            else
            {
                empty++;
            }
        }

        await writer;
        Assert.True(whole > 0, $"No reading was whole ({empty} came back empty)");
    }

    private void WriteAgainWhenRefused(string record)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                AtomicFile.WriteAllText(_home.Path, StartProgressFile.FileName, record);
                return;
            }
            catch (Exception ex) when (ex is (IOException or UnauthorizedAccessException) && attempt < 200)
            {
                Thread.Yield();
            }
        }
    }

    [Theory]
    [InlineData("starting", 0, 0, "starting")]
    [InlineData("opening_database", 0, 0, "opening the database")]
    [InlineData("saving_copy", 0, 0, "saving a copy of the data")]
    [InlineData("migrating", 3, 7, "updating the database (step 3 of 7)")]
    [InlineData("migrating", 0, 0, "updating the database")]
    [InlineData("preparing", 0, 0, "getting ready")]
    [InlineData("starting_listener", 0, 0, "starting to listen")]
    [InlineData("ready", 0, 0, "ready")]
    [InlineData("something_new", 0, 0, "something_new")]
    public void Each_phase_has_its_words_for_the_log(string phase, int step, int steps, string words)
    {
        Assert.Equal(words, new StartProgress(1, phase, step, steps, TimeSpan.Zero, DateTimeOffset.UtcNow).Words);
    }

    private static string Record(int step) =>
        JsonSerializer.Serialize(new { pid = 5764, phase = "migrating", step, steps = 2_000, expect_within_s = 468, at = DateTimeOffset.UtcNow });
}
