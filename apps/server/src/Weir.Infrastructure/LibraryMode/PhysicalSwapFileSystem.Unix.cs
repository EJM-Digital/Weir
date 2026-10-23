using System.Runtime.InteropServices;

namespace Weir.Infrastructure.LibraryMode;

/// <summary>
/// <see cref="PhysicalSwapFileSystem"/>'s POSIX-only interop: the <c>libc</c> calls its cross-platform operations
/// branch into on Linux and other non-Windows platforms, kept apart from those operations' own logic.
/// </summary>
public sealed partial class PhysicalSwapFileSystem
{
    private const int Ebusy = 16;
    private const int Etxtbsy = 26;
    private const int Exdev = 18;

    private const uint StatxNlink = 0x4;
    private const uint StatxUid = 0x8;
    private const uint StatxGid = 0x10;

    internal readonly record struct UnixFileStat(uint Links, uint Uid, uint Gid);

    /// <summary>
    /// What <c>statx</c> says about a path, or null where there is no <c>statx</c> to ask (a libc without it, or a system that is not
    /// Linux) or the answer carries no link count.
    /// </summary>
    private static UnixFileStat? UnixStat(string path) =>
        WhereLibcHasIt(
            () =>
            {
                const int atFdCwd = -100;
                var buffer = new byte[256];
                return Statx(atFdCwd, path, 0, StatxNlink | StatxUid | StatxGid, ref buffer[0]) != 0
                    ? throw StatxFailure(Marshal.GetLastPInvokeError(), path)
                    : StatFrom(buffer);
            },
            whenMissing: null);

    private static bool UnixIsRoot() => WhereLibcHasIt(() => Geteuid() == 0, whenMissing: false);

    /// <summary>The result of <paramref name="call"/>, or <paramref name="whenMissing"/> where the libc does not have the entry point it names.</summary>
    internal static T WhereLibcHasIt<T>(Func<T> call, T whenMissing)
    {
        try
        {
            return call();
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return whenMissing;
        }
    }

    internal static Exception StatxFailure(int errno, string path) =>
        errno == 2 ? new FileNotFoundException($"No such file: '{path}'", path) : new IOException($"statx failed with errno {errno} for '{path}'");

    /// <summary>
    /// Reads what <c>statx</c> filled in. <c>struct statx</c> is the same on every Linux architecture: stx_mask (u32) at 0,
    /// stx_nlink at 16, stx_uid at 20, stx_gid at 24; null when the filesystem gave no link count.
    /// </summary>
    internal static UnixFileStat? StatFrom(byte[] buffer) =>
        (BitConverter.ToUInt32(buffer, 0) & StatxNlink) == 0
            ? null
            : new UnixFileStat(BitConverter.ToUInt32(buffer, 16), BitConverter.ToUInt32(buffer, 20), BitConverter.ToUInt32(buffer, 24));

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int directoryFd, string path, int flags, uint mask, ref byte buffer);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint Geteuid();

    [LibraryImport("libc", EntryPoint = "chown", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnixChown(string path, uint owner, uint group);
}
