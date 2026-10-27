namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>The delivered copy is not byte-identical to the source, so it must not be published.</summary>
public sealed class PassThroughIntegrityException : Exception
{
    public PassThroughIntegrityException(string message)
        : base(message)
    {
    }
}

/// <summary>The library values a delivery needs, copied out so no unit of work is open during the copy.</summary>
public sealed record PassThroughDeliverySettings(
    long LibraryId, string WatchedFolder, string OutputFolder, string? OutputCollisionPolicy, long MinimumFreeDiskSpaceMb);

/// <summary>
/// Whether a pass-through copy was delivered, where, the collision decision, and the sentence describing it.
/// <paramref name="WaitingForSpace"/> is set when nothing was copied because the output drive is short of the space the
/// workflow keeps free; <paramref name="Sentence"/> is then the reason the file is on hold.
/// </summary>
public sealed record PassThroughDeliveryResult(
    bool Delivered, string Destination, CollisionDecision Collision, string Sentence, DiskSpaceCheck? WaitingForSpace = null);

/// <summary>
/// Hands the original back to the output folder unmodified when Weir could not process it (#465).
/// Pure filesystem work; touches no database. The copy is verified as byte-identical to the source, never as valid media:
/// a file that failed processing may be exactly the kind that will not probe, so validating it as media would make
/// pass-through fail on the files that most need it.
/// </summary>
public static class PassThroughDelivery
{
    public static async Task<PassThroughDeliveryResult> DeliverUnchangedAsync(
        PassThroughDeliverySettings settings, string relativePath, IOutputOwnership? ownership = null, Func<string, long>? freeBytes = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var watchedRoot = RemuxPassPaths.Resolve(settings.WatchedFolder);
        var outputRoot = RemuxPassPaths.Resolve(settings.OutputFolder);
        var source = RemuxPassPaths.Resolve(Path.Combine(watchedRoot, relativePath));
        // Resolved (not left as a raw join) so a payload's "/" separators normalize to the platform's own on
        // every host OS.
        var final = RemuxPassPaths.Resolve(Path.Combine(outputRoot, relativePath));

        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"The original is no longer in the watched folder at {source}, so there is nothing to hand back.");
        }

        if (RemuxPassPaths.SamePath(outputRoot, watchedRoot))
        {
            throw new InvalidOperationException("The output folder is the watched folder, so handing the file back would overwrite the original.");
        }

        var decision = OutputCollision.Decide(final, source, source, settings.OutputCollisionPolicy);

        string sentence;
        if (decision.Wrote)
        {
            var disk = FileLifecycle.CheckMinimumFreeDiskSpace(decision.Destination, settings.MinimumFreeDiskSpaceMb, freeBytes);
            if (!disk.Ok)
            {
                return new PassThroughDeliveryResult(
                    false, decision.Destination, decision, DiskSpaceWaits.Reason("output drive", disk.RequiredMb, disk.FreeMb), disk);
            }

            var before = Fingerprint(source);
            Task ValidateStagedAsync(string staged)
            {
                var stagedSize = new FileInfo(staged).Length;
                var after = Fingerprint(source);
                if (after != before)
                {
                    throw new PassThroughIntegrityException("The source changed while Weir was copying it, so the copy was not delivered.");
                }

                if (stagedSize != before.Size)
                {
                    throw new PassThroughIntegrityException(
                        $"The copy is {stagedSize} bytes but the source is {before.Size}, so the copy was not delivered.");
                }

                return Task.CompletedTask;
            }

            await FileLifecycle.SafeCopyToFinalAsync(source, decision.Destination, ValidateStagedAsync, ownership: ownership).ConfigureAwait(false);
            sentence = "Weir could not process this file, so it handed the original back unchanged to " +
                       $"{decision.Destination}. Your media manager can import it as normal.";
        }
        else
        {
            var reason = decision.Reason;
            // Every reason OutputCollision gives is a sentence.
            var lowered = char.ToLowerInvariant(reason[0]) + reason[1..];
            sentence = "Weir could not process this file and would have handed the original back unchanged, " +
                       $"but {lowered}";
        }

        return new PassThroughDeliveryResult(decision.Wrote, decision.Destination, decision, sentence);
    }

    /// <summary>Size plus a modification marker, to catch a source that changed underneath the copy.</summary>
    private static (long Size, long ModifiedTicks) Fingerprint(string path)
    {
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc.Ticks);
    }
}
