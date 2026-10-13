using Microsoft.Extensions.Logging;
using Weir.Core.Configuration;
using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>A server the tray starts again says so in weir.log, as a warning, so a start that only worked on a retry is never invisible.</summary>
public sealed class StartAttemptNoteTests
{
    private static RuntimeEnvironment Environment(params (string Name, string Value)[] variables) =>
        new(variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal), true, "/home/x", "/x");

    [Fact]
    public void A_retry_is_a_warning_that_names_the_attempt_and_why_the_earlier_one_failed()
    {
        var logger = new RecordingLogger<StartAttemptNoteTests>();

        StartAttemptNote.WarnIfRetry(
            Environment((StartAttemptNote.AttemptVariable, "3"), (StartAttemptNote.FailuresVariable, "attempt 1: hung in phase 'opening the database' for 31 s; attempt 2: exited with code 1")),
            logger);

        var warning = Assert.Single(logger.At(LogLevel.Warning));
        Assert.Contains("start attempt 3", warning, StringComparison.Ordinal);
        Assert.Contains("hung in phase 'opening the database' for 31 s", warning, StringComparison.Ordinal);
        Assert.Contains("exited with code 1", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("two")]
    public void A_first_start_or_a_value_that_is_not_an_attempt_number_says_nothing(string? attempt)
    {
        var logger = new RecordingLogger<StartAttemptNoteTests>();

        StartAttemptNote.WarnIfRetry(attempt is null ? Environment() : Environment((StartAttemptNote.AttemptVariable, attempt)), logger);

        Assert.Equal(0, logger.Count);
    }

    [Fact]
    public void A_retry_the_tray_gave_no_reason_for_is_still_a_warning()
    {
        var logger = new RecordingLogger<StartAttemptNoteTests>();

        StartAttemptNote.WarnIfRetry(Environment((StartAttemptNote.AttemptVariable, "2")), logger);

        Assert.Contains("did not say why", Assert.Single(logger.At(LogLevel.Warning)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_very_long_reason_is_cut()
    {
        var logger = new RecordingLogger<StartAttemptNoteTests>();

        StartAttemptNote.WarnIfRetry(Environment((StartAttemptNote.AttemptVariable, "2"), (StartAttemptNote.FailuresVariable, new string('x', 5_000))), logger);

        Assert.True(Assert.Single(logger.At(LogLevel.Warning)).Length < 1_300);
    }
}
