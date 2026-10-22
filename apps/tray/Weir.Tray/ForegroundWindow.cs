using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Weir.Tray;

/// <summary>What <see cref="ForegroundWindow"/> asks of Windows, so the order of its requests can be tested without a desktop that grants them.</summary>
interface IWindowSystem
{
    /// <summary>Asks for the window to be top-most.</summary>
    void PutOnTop(IntPtr window);

    /// <summary>Asks for the window to be the foreground window; whether Windows allowed it.</summary>
    bool SetForeground(IntPtr window);

    /// <summary>Asks again by joining the input of the thread that holds the foreground, which Windows allows.</summary>
    void TakeForegroundThroughTheHolder(IntPtr window);

    IntPtr Foreground();

    /// <summary>Flashes the window's taskbar button until the person comes to it.</summary>
    void Flash(IntPtr window);
}

/// <summary>
/// Puts a window the person must answer in front of whatever else is open. Windows only lets the process that owns the
/// foreground hand it on, and a tray started by hand or at sign-in does not own it, so a plain <see cref="Form.Activate"/>
/// leaves the window behind Server Manager or whatever else is up. The window is made top-most, the foreground is taken the way
/// Windows allows (by joining the input of the thread that holds it), and when it still cannot be taken the taskbar button
/// flashes until the person comes to it.
/// </summary>
static class ForegroundWindow
{
    /// <summary>Makes <paramref name="form"/> top-most and the foreground window, or flashes its taskbar button when Windows will not allow that.</summary>
    [ExcludeFromCodeCoverage]
    internal static void Bring(Form form) => Bring(form, Win32Windows.Instance);

    internal static void Bring(Form form, IWindowSystem windows)
    {
        var handle = form.Handle;
        windows.PutOnTop(handle);
        form.Activate();
        if (!windows.SetForeground(handle))
        {
            windows.TakeForegroundThroughTheHolder(handle);
        }
        if (windows.Foreground() != handle)
        {
            windows.Flash(handle);
        }
    }
}

/// <summary>The calls <see cref="ForegroundHolder"/> makes to join the input of the thread that holds the foreground.</summary>
interface IInputThreads
{
    /// <summary>The thread that owns the foreground window, or 0 when no window is in front.</summary>
    uint HolderThread();

    uint CurrentThread();

    /// <summary>Joins the input of <paramref name="holder"/>; whether Windows allowed it.</summary>
    bool Join(uint thread, uint holder);

    void Leave(uint thread, uint holder);

    void RequestForeground(IntPtr window);
}

/// <summary>Takes the foreground by joining the input of the thread that holds it, the one way Windows allows a process that does not own the foreground to take it.</summary>
static class ForegroundHolder
{
    internal static void Take(IntPtr window, IInputThreads threads)
    {
        var holder = threads.HolderThread();
        var current = threads.CurrentThread();
        if (holder == 0 || holder == current || !threads.Join(current, holder))
        {
            return;
        }
        try
        {
            threads.RequestForeground(window);
        }
        finally
        {
            threads.Leave(current, holder);
        }
    }
}

/// <summary>Windows itself: each member is one call into user32 or kernel32, which only a desktop answers.</summary>
[ExcludeFromCodeCoverage]
sealed class Win32Windows : IWindowSystem, IInputThreads
{
    internal static readonly Win32Windows Instance = new();

    private static readonly IntPtr TopMost = new(-1);
    private const uint NoMove = 0x0002;
    private const uint NoSize = 0x0001;
    private const uint ShowWindow = 0x0040;
    private const uint FlashAll = 0x0003;
    private const uint FlashUntilForeground = 0x000C;

    public void PutOnTop(IntPtr window) => SetWindowPos(window, TopMost, 0, 0, 0, 0, NoMove | NoSize | ShowWindow);

    public bool SetForeground(IntPtr window) => SetForegroundWindow(window);

    public IntPtr Foreground() => GetForegroundWindow();

    public void TakeForegroundThroughTheHolder(IntPtr window) => ForegroundHolder.Take(window, this);

    public uint HolderThread() => GetWindowThreadProcessId(GetForegroundWindow(), out _);

    public uint CurrentThread() => GetCurrentThreadId();

    public bool Join(uint thread, uint holder) => AttachThreadInput(thread, holder, true);

    public void Leave(uint thread, uint holder) => AttachThreadInput(thread, holder, false);

    public void RequestForeground(IntPtr window) => SetForegroundWindow(window);

    public void Flash(IntPtr window)
    {
        var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = window, Flags = FlashAll | FlashUntilForeground, Count = uint.MaxValue };
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool attached);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
