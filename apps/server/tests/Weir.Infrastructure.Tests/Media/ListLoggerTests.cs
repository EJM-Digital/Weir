using Microsoft.Extensions.Logging;

namespace Weir.Infrastructure.Tests.Media;

/// <summary>
/// <see cref="ListLogger{T}"/> is written by a service's threads while a test reads it, so a test that is looking through
/// the entries must be able to see another entry arrive.
/// </summary>
public sealed class ListLoggerTests
{
    [Fact]
    public void An_entry_logged_while_the_entries_are_being_read_does_not_break_the_read()
    {
        var logger = new ListLogger<ListLoggerTests>();
        logger.LogInformation("first");

        var read = 0;
        foreach (var _ in logger.Entries)
        {
            logger.LogWarning("arrived during the read");
            read++;
        }

        Assert.Equal(1, read);
        Assert.Equal(2, logger.Entries.Count);
    }

    [Fact]
    public void An_exception_logged_while_the_exceptions_are_being_read_does_not_break_the_read()
    {
        var logger = new ListLogger<ListLoggerTests>();
        logger.LogError(new InvalidOperationException("first"), "first");

        var read = 0;
        foreach (var _ in logger.Exceptions)
        {
            logger.LogError(new InvalidOperationException("arrived during the read"), "second");
            read++;
        }

        Assert.Equal(1, read);
        Assert.Equal(2, logger.Exceptions.Count);
    }
}
