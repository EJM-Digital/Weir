using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Weir.Tray;

/// <summary>
/// Closes any stdio handles Weir.exe inherited from whatever started it, before it settles in as a
/// long-running background app.
///
/// A caller that redirects a child process's stdout and stderr through pipes and waits for those
/// pipes to reach end-of-file — the ordinary way to capture an installer's output — blocks until
/// every process holding the write end closes it, not just the process it launched. Windows hands
/// the same inheritable handles down to whatever that process spawns in turn. Weir.exe runs until
/// Quit, and starts its own child, WeirServer.exe; left alone, both would hold an inherited pipe
/// open indefinitely, and a caller waiting for end-of-file would wait forever too, even though the
/// process it actually launched (Setup.exe, or Weir.exe itself) already exited (#779). Closing the
/// handles here, before anything else runs, stops them reaching WeirServer.exe as well.
///
/// What stands in their place is the null device, not nothing: a write to it returns at once and a read finds the end of the file,
/// so nothing the tray or a process it starts without redirecting does with its standard handles can wait on a pipe that nobody
/// reads. (A write into a full, unread pipe waits for ever, and so does opening the console stream of a process whose handle is
/// that pipe: that is how a server was found not to start. The server now never waits for its console, and the tray gives it
/// pipes of its own that it reads, but the tray itself should not be one more process that can be held there.)
/// </summary>
static class InheritedStdioHandles
{
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    // Classic DllImport, not LibraryImport: the source-generated form needs <AllowUnsafeBlocks> for
    // its bool-returning marshalling, which is more than this one file needs to ask of the project.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int nStdHandle, nint hHandle);

    // Kept for the life of the process: the standard handles point at it.
    private static SafeFileHandle? _nullDevice;

    private static readonly (string Name, int Id)[] Handles =
    [
        ("stdin", StdInputHandle),
        ("stdout", StdOutputHandle),
        ("stderr", StdErrorHandle),
    ];

    /// <summary>
    /// Best-effort: closing a handle Weir never opened itself must never stop Weir from starting, so
    /// every failure here is logged and skipped rather than thrown.
    /// </summary>
    internal static void CloseInherited(Action<string> log)
    {
        foreach (var (name, id) in Handles)
        {
            var handle = GetStdHandle(id);

            // No handle was ever inherited (the common case: a person double-clicking Weir, or a
            // launcher that did not redirect it), or it is already invalid.
            if (handle == nint.Zero || handle == new nint(-1))
            {
                continue;
            }

            if (!CloseHandle(handle))
            {
                log($"Could not close the inherited {name} handle (Win32 error {Marshal.GetLastWin32Error()}).");
                continue;
            }

            SetStdHandle(id, NullDevice(log));
        }
    }

    // The null device, opened once; zero (no handle) if it cannot be, which is what the tray had before.
    private static nint NullDevice(Action<string> log)
    {
        try
        {
            _nullDevice ??= File.OpenHandle("NUL", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return _nullDevice.DangerousGetHandle();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log($"Could not open the null device for the standard handles ({ex.Message}).");
            return nint.Zero;
        }
    }
}
