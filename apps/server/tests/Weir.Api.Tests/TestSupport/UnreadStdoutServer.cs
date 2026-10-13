using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Weir.Api.Tests;

/// <summary>
/// The real server (<c>Weir.dll</c> from this test's output) started with its standard output and error set to a pipe that is
/// already full and that nobody will ever read: what a server gets when whatever started it keeps a pipe and does not drain it.
/// .NET's own <see cref="Process"/> cannot hand a child a pipe that is full, so the process is created with CreateProcess.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class UnreadStdoutServer : IDisposable
{
    private const int InheritHandles = 1;
    private const int UseStdHandles = 0x100;
    private const int CreateNoWindow = 0x08000000;
    private const int CreateUnicodeEnvironment = 0x400;
    private const int PipeBufferBytes = 4096;
    private const int PipeWait = 0;
    private const int PipeNoWait = 1;

    private readonly Process _process;
    private readonly int _port;

    private UnreadStdoutServer(Process process, int port, string home)
    {
        _process = process;
        _port = port;
        Home = home;
    }

    internal string Home { get; }

    /// <summary>Whether the server is running and has been seen to exit; null while it runs.</summary>
    internal int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    /// <summary>Starts the server on <paramref name="port"/> with <paramref name="environment"/> added to this process's, and a stdout pipe that is full.</summary>
    internal static UnreadStdoutServer Start(string home, int port, IReadOnlyDictionary<string, string> environment)
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = InheritHandles };
        Check(CreatePipe(out var outRead, out var outWrite, ref security, PipeBufferBytes), "CreatePipe (out)");
        Check(CreatePipe(out var inRead, out _, ref security, PipeBufferBytes), "CreatePipe (in)");

        FillAndLeaveAWriterWaiting(outWrite);

        var program = Path.Combine(AppContext.BaseDirectory, "Weir.dll");
        var info = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>(),
            Flags = UseStdHandles,
            StdInput = inRead,
            StdOutput = outWrite,
            StdError = outWrite,
        };
        var variables = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in environment)
        {
            variables[name] = value;
        }

        var block = string.Join('\0', variables.Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";
        var command = $"dotnet \"{program}\" --host 127.0.0.1 --port {port.ToString(CultureInfo.InvariantCulture)}";
        Check(
            CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, true, CreateNoWindow | CreateUnicodeEnvironment, block, AppContext.BaseDirectory, ref info, out var created),
            "CreateProcess");
        return new UnreadStdoutServer(Process.GetProcessById(created.ProcessId), port, home);
    }

    /// <summary>Waits for the server to exit, for no longer than <paramref name="ceiling"/>; false when it is still running.</summary>
    internal async Task<bool> ExitsAsync(TimeSpan ceiling)
    {
        using var cancelled = new CancellationTokenSource(ceiling);
        try
        {
            await _process.WaitForExitAsync(cancelled.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Waits until /ready answers 200, or <paramref name="ceiling"/> passes: the wait is on the server, and only a broken one reaches the ceiling.</summary>
    internal async Task<bool> BecomesReadyAsync(TimeSpan ceiling)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var cancelled = new CancellationTokenSource(ceiling);
        while (!cancelled.IsCancellationRequested && !_process.HasExited)
        {
            try
            {
                using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{_port}/ready"), cancelled.Token);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // Not listening yet.
            }

            await Task.Delay(100, CancellationToken.None);
        }

        return false;
    }

    public void Dispose()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(10_000);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }

        _process.Dispose();
    }

    // Fills the pipe to the last byte, then leaves a write waiting on it. The pipe is put in non-waiting mode for the filling, in which a
    // write that does not fit returns at once, so "full" is known and not waited for; it is put back, and one more write is made on a thread
    // of its own that can never finish. That waiting write is part of the condition (the server's own console calls queue behind it),
    // and it is already waiting by the time a server can start: the thread is started first, and a server takes far longer than the call.
    private static void FillAndLeaveAWriterWaiting(IntPtr write)
    {
        var noWait = PipeNoWait;
        Check(SetNamedPipeHandleState(write, ref noWait, IntPtr.Zero, IntPtr.Zero), "SetNamedPipeHandleState (no wait)");
        var oneByte = new byte[1];
        while (WriteFile(write, oneByte, oneByte.Length, out var written, IntPtr.Zero) && written == oneByte.Length)
        {
        }

        var wait = PipeWait;
        Check(SetNamedPipeHandleState(write, ref wait, IntPtr.Zero, IntPtr.Zero), "SetNamedPipeHandleState (wait)");
        var aboutToWrite = new ManualResetEventSlim(false);
        new Thread(() =>
        {
            aboutToWrite.Set();
            WriteFile(write, oneByte, oneByte.Length, out _, IntPtr.Zero);
        })
        { IsBackground = true }.Start();
        aboutToWrite.Wait();
    }

    private static void Check(bool succeeded, string what)
    {
        if (!succeeded)
        {
            throw new InvalidOperationException($"{what} failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int Inherit;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetNamedPipeHandleState(IntPtr pipe, ref int mode, IntPtr maxCollectionCount, IntPtr collectDataTimeout);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(IntPtr file, byte[] buffer, int count, out int written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string? application,
        string command,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        int flags,
        string environment,
        string directory,
        ref StartupInfo startup,
        out ProcessInformation created);
}
