using System.Security.Cryptography;
using Weir.Core.LibraryMode;

namespace Weir.Infrastructure.LibraryMode;

/// <summary>
/// #735: a kept original was found already at its destination, but its content does not match the backup it came
/// from — a crash mid-copy a retry cannot resolve by itself. Distinct from other <see cref="IOException"/>s so
/// <see cref="SwapRecoverySweep"/> can record it once and stop retrying, rather than treating it as a transient
/// problem (a lock, a permissions error) worth trying again next time.
/// </summary>
public sealed class OriginalsKeepConflictException : IOException
{
    public OriginalsKeepConflictException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// #735: reserves and fills a library swap's kept-original destination instead of just deleting the backup, per
/// docs/file-lifecycle-contract.md's cross-volume rule. Shared by <see cref="SafeSwap"/>'s commit step and
/// <see cref="SwapRecoverySweep"/>'s recovery of a crash that interrupted it.
/// </summary>
public static class OriginalsMover
{
    /// <summary>What <see cref="ISwapFileSystem.TryReserve"/> leaves behind before it is filled: nothing.</summary>
    private static readonly string EmptyContentHash = Convert.ToHexString(SHA256.HashData(ReadOnlySpan<byte>.Empty));

    /// <summary>
    /// Atomically claims a free name for a kept original, starting at <paramref name="candidate"/> and trying the next
    /// numbered suffix (<see cref="OriginalsPathPlanner.CandidateAt"/>) when a name is already taken — including by
    /// another swap reserving the very same name at the same moment, which a plain existence check made just before this
    /// call could never see. Returns the reserved (still-empty) path; the caller fills it with <see cref="Fill"/>.
    /// </summary>
    public static string Reserve(ISwapFileSystem files, string candidate, int limit = 999)
    {
        ArgumentNullException.ThrowIfNull(files);
        var directory = DirectoryOf(candidate);
        if (directory is not null)
        {
            files.EnsureDirectory(directory);
        }

        for (var attempt = 1; attempt <= limit; attempt++)
        {
            var next = OriginalsPathPlanner.CandidateAt(candidate, attempt);
            if (files.TryReserve(next))
            {
                return next;
            }
        }

        throw new IOException($"Weir could not find a free name for the kept original near '{candidate}' after {limit} attempts.");
    }

    /// <summary>
    /// Fills <paramref name="destination"/> — a name already reserved by <see cref="Reserve"/> — with
    /// <paramref name="source"/>'s bytes: a same-volume rename, or (when they are on different volumes) a copy verified
    /// against the source's content before the source is deleted. Replacing <paramref name="destination"/> is safe here,
    /// and only here, because nothing but the reservation holder could ever have put anything at that exact name.
    /// </summary>
    public static void Fill(ISwapFileSystem files, string source, string destination)
    {
        ArgumentNullException.ThrowIfNull(files);
        try
        {
            files.ReplaceReservation(source, destination);
            return;
        }
        catch (CrossVolumeException)
        {
        }

        var sourceHash = files.ContentHash(source);
        files.CopyOverReservation(source, destination);
        if (files.ContentHash(destination) != sourceHash)
        {
            // A failure this method can see (as opposed to a crash mid-copy, which OriginalsMover.Recover handles):
            // clean it up so a later retry starts from a fresh reservation rather than tripping over this one.
            files.Delete(destination);
            throw new IOException($"The copy to '{destination}' did not match the original's contents. The original was left in place.");
        }

        files.Delete(source);
    }

    /// <summary>
    /// Finishes a keep the recovery sweep found interrupted, using <paramref name="destination"/> exactly as the journal
    /// recorded it (already reserved by the original attempt):
    /// <list type="bullet">
    /// <item>Nothing there, or an empty reservation that was never filled → run <see cref="Fill"/> now, as the first attempt would have.</item>
    /// <item>Content matching the backup → the fill already finished; only the leftover backup is removed.</item>
    /// <item>Anything else → a crash left mismatched, non-empty content: recorded once as a conflict rather than guessed at.</item>
    /// </list>
    /// </summary>
    public static void Recover(ISwapFileSystem files, string source, string destination)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.FileExists(destination))
        {
            var destinationHash = files.ContentHash(destination);
            if (destinationHash == EmptyContentHash)
            {
                Fill(files, source, destination);
                return;
            }

            if (destinationHash == files.ContentHash(source))
            {
                files.Delete(source);
                return;
            }

            throw new OriginalsKeepConflictException(
                $"The kept original at '{destination}' does not match its backup '{source}'; neither was touched.");
        }

        Fill(files, source, destination);
    }

    /// <summary>
    /// Best-effort cleanup for a swap that reserved a kept-original destination and then rolled back before committing
    /// (the original changed underneath Weir): removes the reservation only while it is still the empty placeholder
    /// <see cref="Reserve"/> left, so a destination some later, unrelated attempt has since filled is never touched.
    /// Never throws; a failure here does not affect the rollback it is cleaning up after.
    /// </summary>
    public static void CleanUpUnfilledReservation(ISwapFileSystem files, string destination)
    {
        ArgumentNullException.ThrowIfNull(files);
        try
        {
            if (files.FileExists(destination) && files.ContentHash(destination) == EmptyContentHash)
            {
                files.Delete(destination);
            }
        }
#pragma warning disable CA1031 // Best-effort: the rollback it runs alongside must never fail because of this.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
        }
    }

    private static string? DirectoryOf(string path)
    {
        var lastSeparator = path.LastIndexOfAny(['\\', '/']);
        return lastSeparator <= 0 ? null : path[..lastSeparator];
    }
}
