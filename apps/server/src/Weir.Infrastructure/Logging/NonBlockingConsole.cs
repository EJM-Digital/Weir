using System.Text;
using System.Threading.Channels;

namespace Weir.Infrastructure.Logging;

/// <summary>
/// Weir's lines on standard output (or error), written by a thread of their own so that nothing in the server ever waits for them.
/// A pipe has room for a few kilobytes; when whatever holds its other end does not read, a write into it waits for ever. A log call
/// that wrote to the console itself would wait with it, and so would the start of the server: it would never reach its own log, never
/// listen, never create the event the tray stops it by and never exit. Here a line is handed to a bounded queue and the call returns;
/// when the queue is full the line is dropped and counted, and the thread says how many it dropped as soon as the console takes
/// another line. The same lines are always in <c>weir.log</c>, which is a file.
/// </summary>
public sealed class NonBlockingConsole : IDisposable
{
    /// <summary>How many lines wait for the console before further ones are dropped.</summary>
    public const int Capacity = 1024;

    /// <summary>How long stopping waits for the last lines to reach a console that is taking them; a console that is not is left.</summary>
    public static readonly TimeSpan ShutdownDrain = TimeSpan.FromSeconds(1);

    private readonly Func<Stream> _open;
    private readonly Func<Encoding> _encoding;
    private readonly Channel<string> _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });

    private readonly Thread _writer;
    private Stream? _output;
    private Encoding? _bytes;
    private long _dropped;
    private long _written;
    private long _queued;

    /// <param name="open">Opens the raw stream the lines go to (standard output or error). It is called on the writing thread, never on the caller's.</param>
    /// <param name="encoding">How the lines are encoded: the console's own, which is what its reader decodes with. Also called on the writing thread.</param>
    public NonBlockingConsole(Func<Stream> open, Func<Encoding> encoding)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _encoding = encoding ?? throw new ArgumentNullException(nameof(encoding));
        _writer = new Thread(WriteLines) { IsBackground = true, Name = "Weir console" };
        _writer.Start();
    }

    /// <summary>A console that writes to <paramref name="output"/> in <paramref name="encoding"/>.</summary>
    public NonBlockingConsole(Stream output, Encoding encoding)
        : this(() => output, () => encoding)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(encoding);
    }

    /// <summary>
    /// Standard output, in the encoding the console uses. The stream is opened on the writing thread: with a pipe that is full and not
    /// read, even <c>Console.OpenStandardOutput()</c> does not return (measured), so opening it here would hold up the caller.
    /// </summary>
    public static NonBlockingConsole ForStandardOutput() => new(Console.OpenStandardOutput, () => Console.OutputEncoding);

    /// <summary>Standard error, opened on the writing thread like <see cref="ForStandardOutput"/>.</summary>
    public static NonBlockingConsole ForStandardError() => new(Console.OpenStandardError, () => Console.OutputEncoding);

    /// <summary>How many lines were dropped because the console was not taking them.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Hands <paramref name="line"/> to the console and returns at once, whatever the console is doing.</summary>
    public void WriteLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (_lines.Writer.TryWrite(line))
        {
            Interlocked.Increment(ref _queued);
        }
        else
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>
    /// Waits, for no longer than <paramref name="limit"/>, for what was handed over to reach the console. For a process that is about to
    /// exit and wants its last words out when it can, without waiting for a console that will not take them.
    /// </summary>
    public bool Drain(TimeSpan limit)
    {
        var deadline = Environment.TickCount64 + (long)limit.TotalMilliseconds;
        while (Interlocked.Read(ref _written) < Interlocked.Read(ref _queued))
        {
            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            Thread.Sleep(5);
        }

        return true;
    }

    /// <summary>Gives what is queued a moment to reach the console (<see cref="ShutdownDrain"/>), then stops taking lines.</summary>
    public void Dispose()
    {
        Drain(ShutdownDrain);
        _lines.Writer.TryComplete();
    }

    private void WriteLines()
    {
        var reader = _lines.Reader;
        var open = TryOpen();
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var line))
            {
                if (open)
                {
                    open = TryWrite(line);
                }

                Interlocked.Increment(ref _written);
            }
        }
    }

    // A console that cannot be opened, or that closes, is the same to the server as a process with no console: the lines go nowhere.
    private bool TryOpen()
    {
        try
        {
            _output = _open();
            _bytes = _encoding();
            return true;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryWrite(string line)
    {
        try
        {
            WriteDropNotice();
            WriteOne(line);
            return true;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void WriteDropNotice()
    {
        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
        {
            WriteOne($"({dropped} console lines were not written because nothing was reading standard output; weir.log has them all.)");
        }
    }

    private void WriteOne(string line)
    {
        var bytes = _bytes!.GetBytes(line + Environment.NewLine);
        _output!.Write(bytes, 0, bytes.Length);
        _output.Flush();
    }
}
