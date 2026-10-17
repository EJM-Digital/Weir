namespace Weir.Core.Media;

/// <summary>A failure whose own message is not for a person, and that carries the sentence a person reads in its place.</summary>
public interface IPlainFailure
{
    /// <summary>
    /// What a person reads in place of <see cref="Exception.Message"/> when that message is a tool's or the system's own text (stderr,
    /// file paths, memory addresses), which belongs in technical detail only. Null when the message is a sentence Weir wrote.
    /// </summary>
    string? PlainMessage { get; }
}

/// <summary>
/// ffprobe or ffmpeg failed, returned nothing usable, or the output did not validate.
/// </summary>
public class MediaToolException : Exception, IPlainFailure
{
    public MediaToolException()
    {
    }

    public MediaToolException(string message)
        : base(message)
    {
    }

    public MediaToolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public string? PlainMessage { get; init; }
}

/// <summary>
/// ffprobe ran and could not read the file's contents: the file itself is bad.
/// </summary>
/// <remarks>
/// Distinct from ffprobe being missing, timing out or being denied access, which say nothing about
/// the release. Only this one is evidence a manager should look for a different release.
/// </remarks>
public sealed class MediaUnreadableException : MediaToolException
{
    public MediaUnreadableException()
    {
    }

    public MediaUnreadableException(string message)
        : base(message)
    {
    }

    public MediaUnreadableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A staged or source media file cannot be trusted as complete.</summary>
public sealed class MediaCompletenessException : MediaToolException
{
    public MediaCompletenessException()
    {
    }

    public MediaCompletenessException(string message)
        : base(message)
    {
    }

    public MediaCompletenessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A tool ran past its time limit. Not a <see cref="MediaToolException"/>, so a handler for tool failures does
/// not also catch a timeout. The message has the form the golden files record
/// (<c>Command '[...]' timed out after 120 seconds</c>).
/// </summary>
public sealed class MediaToolTimeoutException : TimeoutException
{
    public MediaToolTimeoutException()
    {
    }

    public MediaToolTimeoutException(string message)
        : base(message)
    {
    }

    public MediaToolTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
