using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Weir.Tray;

/// <summary>The calls <see cref="InheritedStdioHandles"/> makes on the process's standard handles. A test supplies its own.</summary>
interface IStdHandleCalls
{
    /// <summary>The handle value for a standard handle id (stdin, stdout, stderr); zero or -1 when there is none.</summary>
    nint Get(int id);

    /// <summary>Closes a handle; false (with the error in <paramref name="error"/>) when it could not be.</summary>
    bool Close(nint handle, out int error);

    /// <summary>Makes <paramref name="handle"/> the standard handle for <paramref name="id"/>.</summary>
    bool Set(int id, nint handle, out int error);

    /// <summary>The null device, opened for reading and writing; throws when it cannot be.</summary>
    nint OpenNullDevice();
}

/// <summary>
/// Closes any stdio handles Weir.exe inherited from whatever started it, before it settles in as a long-running background app.
///
/// A caller that redirects a child process's stdout and stderr through pipes and waits for those pipes to reach end-of-file (the
/// ordinary way to capture an installer's output) blocks until every process holding the write end closes it, not just the process it
/// launched. Windows hands the same inheritable handles down to whatever that process spawns in turn. Weir.exe runs until Quit, and
/// starts its own child, WeirServer.exe; left alone, both would hold an inherited pipe open indefinitely, and a caller waiting for
/// end-of-file would wait forever too, even though the process it actually launched (Setup.exe, or Weir.exe itself) already exited
/// (#779). Closing the handles here, before anything else runs, stops them reaching WeirServer.exe as well.
///
/// The handles are replaced by the null device, not by nothing: a write to it returns at once and a read finds the end of the file, so
/// nothing the tray does with its standard handles can wait on a pipe that nobody reads. A write into a full pipe that is not read
/// waits for ever, and so does opening the console stream of a process whose standard handle is that pipe.
///
/// The null device is opened first, before anything is closed, so that its handle value cannot be one of the values about to be
/// closed; each distinct value is closed once, however many of the three share it; and the null device itself is never closed.
/// </summary>
static class InheritedStdioHandles
{
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    private static readonly (string Name, int Id)[] Handles =
    [
        ("stdin", StdInputHandle),
        ("stdout", StdOutputHandle),
        ("stderr", StdErrorHandle),
    ];

    /// <summary>
    /// Best-effort: closing a handle Weir never opened itself must never stop Weir from starting, so every failure here is logged and
    /// skipped rather than thrown.
    /// </summary>
    internal static void CloseInherited(Action<string> log) => CloseInherited(WindowsStdHandles.Instance, log);

    internal static void CloseInherited(IStdHandleCalls calls, Action<string> log)
    {
        var nullDevice = OpenNullDevice(calls, log);

        var inherited = Handles.Select(handle => (handle.Name, handle.Id, Value: calls.Get(handle.Id))).ToList();
        var closed = new HashSet<nint>();
        foreach (var (name, _, value) in inherited)
        {
            // No handle was ever inherited (the common case: a person double-clicking Weir, or a launcher that did not redirect it), it
            // is invalid, it is the null device already, or another standard handle shared this value and it has been closed.
            if (value == nint.Zero || value == new nint(-1) || value == nullDevice || !closed.Add(value))
            {
                continue;
            }

            if (!calls.Close(value, out var error))
            {
                log($"Could not close the inherited {name} handle (Win32 error {error}).");
            }
        }

        // Every standard handle ends up on the null device, or on nothing when it could not be opened (what the tray had before).
        foreach (var (name, id, _) in inherited)
        {
            if (!calls.Set(id, nullDevice, out var error))
            {
                log($"Could not point the {name} handle at {(nullDevice == nint.Zero ? "nothing" : "the null device")} (Win32 error {error}).");
            }
        }
    }

    private static nint OpenNullDevice(IStdHandleCalls calls, Action<string> log)
    {
        try
        {
            return calls.OpenNullDevice();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log($"Could not open the null device for the standard handles ({ex.Message}).");
            return nint.Zero;
        }
    }
}

/// <summary>The real standard handles of this process.</summary>
sealed class WindowsStdHandles : IStdHandleCalls
{
    internal static WindowsStdHandles Instance { get; } = new();

    // Kept for the life of the process: the standard handles point at it, and it is never closed.
    private SafeFileHandle? _nullDevice;

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

    public nint Get(int id) => GetStdHandle(id);

    public bool Close(nint handle, out int error)
    {
        var closed = CloseHandle(handle);
        error = closed ? 0 : Marshal.GetLastWin32Error();
        return closed;
    }

    public bool Set(int id, nint handle, out int error)
    {
        var set = SetStdHandle(id, handle);
        error = set ? 0 : Marshal.GetLastWin32Error();
        return set;
    }

    public nint OpenNullDevice()
    {
        _nullDevice ??= File.OpenHandle("NUL", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        return _nullDevice.DangerousGetHandle();
    }
}
