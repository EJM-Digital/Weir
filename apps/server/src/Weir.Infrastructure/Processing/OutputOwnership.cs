using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Weir.Core.Configuration;

namespace Weir.Infrastructure.Processing;

/// <summary>
/// Applies the optional Processing output-ownership policy (#555: <c>WEIR_CHOWN_OUTPUT</c>, <c>WEIR_FILE_MODE_OUTPUT</c>,
/// <c>WEIR_DIR_MODE_OUTPUT</c>) to a file Weir just published, or a folder it just created to publish into. Called
/// from the existing finalize points — <see cref="Weir.Infrastructure.Processing.RemuxPass.FileLifecycle"/>'s
/// <c>SafeCopyToFinalAsync</c>/<c>TryHardlinkToFinalAsync</c>/<c>SafeFinalizeFile</c> (remux pass publish and
/// pass-through delivery both go through these) and <see cref="Weir.Infrastructure.LibraryMode.SafeSwap"/>'s commit
/// rename (library mode). Never throws: a failure here must never fail the job that just finished writing the file.
/// </summary>
public interface IOutputOwnership
{
    /// <summary>Apply the configured owner and <c>WEIR_FILE_MODE_OUTPUT</c> to a file Weir just published.</summary>
    void ApplyToFile(string path);

    /// <summary>Apply the configured owner and <c>WEIR_DIR_MODE_OUTPUT</c> to a folder Weir just created.</summary>
    void ApplyToDirectory(string path);
}

/// <summary>
/// The raw POSIX primitives <see cref="OutputOwnership"/> calls, extracted so the service is testable without a
/// Linux box (#555): a fake implementation proves what <see cref="OutputOwnership"/> decided to do, and the real one
/// (<see cref="LinuxOutputOwnershipTools"/>) is proven separately against the real filesystem, skipped off Linux.
/// </summary>
public interface IOutputOwnershipTools
{
    void Chown(string path, uint uid, uint gid);

    void SetMode(string path, UnixFileMode mode);
}

/// <summary>
/// Real <c>chown(2)</c> (via libc, the same P/Invoke shape as
/// <see cref="Weir.Infrastructure.LibraryMode.PhysicalSwapFileSystem"/>'s permission copy) and .NET's own
/// <see cref="File.SetUnixFileMode(string, UnixFileMode)"/> for the mode bits.
/// </summary>
public sealed partial class LinuxOutputOwnershipTools : IOutputOwnershipTools
{
    public void Chown(string path, uint uid, uint gid)
    {
        if (UnixChown(path, uid, gid) != 0)
        {
            throw new IOException($"chown failed with errno {Marshal.GetLastPInvokeError()} for '{path}'");
        }
    }

    public void SetMode(string path, UnixFileMode mode)
    {
        // Guards, not just documents: File.SetUnixFileMode itself throws PlatformNotSupportedException on Windows,
        // but only this explicit check tells the platform-compatibility analyzer the call below is reachable only
        // where it is supported (this type is registered only when !OperatingSystem.IsWindows() — see
        // WeirPlatformServices.AddWeirPlatform).
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("LinuxOutputOwnershipTools.SetMode is Linux-only.");
        }

        File.SetUnixFileMode(path, mode);
    }

    [LibraryImport("libc", EntryPoint = "chown", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnixChown(string path, uint owner, uint group);
}

/// <summary>
/// Windows has no POSIX owner or mode bits, so this does nothing; registering it (instead of
/// <see cref="LinuxOutputOwnershipTools"/>) is itself the "ignored on Windows" behaviour #555 asks for. The one-time
/// startup log note lives in <c>WeirPlatformServices.AddWeirPlatform</c>, where this is chosen.
/// </summary>
public sealed class WindowsOutputOwnershipTools : IOutputOwnershipTools
{
    public void Chown(string path, uint uid, uint gid)
    {
    }

    public void SetMode(string path, UnixFileMode mode)
    {
    }
}

/// <inheritdoc cref="IOutputOwnership"/>
public sealed class OutputOwnership : IOutputOwnership
{
    private readonly WeirOptions _options;
    private readonly IOutputOwnershipTools _tools;
    private readonly ILogger<OutputOwnership> _logger;

    public OutputOwnership(WeirOptions options, IOutputOwnershipTools tools, ILogger<OutputOwnership> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _tools = tools;
        _logger = logger;
    }

    public void ApplyToFile(string path) => Apply(path, _options.OutputOwnershipFileMode);

    public void ApplyToDirectory(string path) => Apply(path, _options.OutputOwnershipDirectoryMode);

    private void Apply(string path, UnixFileMode? mode)
    {
        if (!_options.OutputOwnershipChownEnabled && mode is null)
        {
            return;
        }

        try
        {
            if (_options.OutputOwnershipChownEnabled)
            {
                _tools.Chown(path, _options.OutputOwnershipUid, _options.OutputOwnershipGid);
            }

            if (mode is not null)
            {
                _tools.SetMode(path, mode.Value);
            }
        }
#pragma warning disable CA1031 // #555: an ownership or mode failure never fails the job that just wrote the file.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception, "Weir could not apply the output ownership policy to {Path}.", path);
        }
    }
}
