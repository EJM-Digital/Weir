using Weir.Core.Configuration;

namespace Weir.Infrastructure.LibraryMode;

/// <summary>The places a swap passes on its way from the original file to the cleaned one, in order.</summary>
public enum SwapPoint
{
    /// <summary>The swap is recorded as started and nothing has been written yet.</summary>
    Started,

    /// <summary>The cleaned copy is written and has passed its checks. The original has not been touched.</summary>
    CopyChecked,

    /// <summary>The swap is recorded as committing and, when the original is kept, the place it will go is reserved.</summary>
    CommitRecorded,

    /// <summary>The original has been renamed to its backup name, so nothing is under the original name.</summary>
    OriginalMovedAside,

    /// <summary>The cleaned copy has been renamed to the original name. This is the commit.</summary>
    CleanedCopyInPlace,

    /// <summary>The commit is recorded.</summary>
    CommittedRecorded,

    /// <summary>The backup has been deleted, or moved to where the original is kept.</summary>
    BackupDealtWith,

    /// <summary>The swap is recorded as finished.</summary>
    FinishedRecorded,
}

/// <summary>Told each time a swap reaches one of its <see cref="SwapPoint"/>s. Implementations must not throw.</summary>
public interface ISwapProgress
{
    void Reached(SwapPoint point);
}

/// <summary>
/// Stops a swap for good at one point so a test can end the process exactly there (<c>WEIR_LIBRARY_SWAP_HOLD_AT</c>, the name of
/// a <see cref="SwapPoint"/>). Reaching the point writes its name to <see cref="HeldFileName"/> in Weir's home and then waits for ever;
/// the test sees the file and stops the process. It is never set on a real install.
/// </summary>
public sealed class HeldSwapProgress : ISwapProgress
{
    /// <summary>The file in Weir's home that says which point the swap is held at.</summary>
    public const string HeldFileName = "swap-held-at.txt";

    private readonly SwapPoint _point;
    private readonly string _home;

    public HeldSwapProgress(SwapPoint point, string home)
    {
        _point = point;
        _home = home;
    }

    /// <summary>The hold the setting asks for. A value that names no point is refused at start-up, not ignored.</summary>
    public static HeldSwapProgress FromSetting(string setting, string home)
    {
        if (!Enum.TryParse<SwapPoint>(setting, ignoreCase: true, out var point) || !Enum.IsDefined(point))
        {
            throw new WeirConfigurationException(
                $"WEIR_LIBRARY_SWAP_HOLD_AT is '{setting}', which is not one of: {string.Join(", ", Enum.GetNames<SwapPoint>())}.");
        }

        return new HeldSwapProgress(point, home);
    }

    public void Reached(SwapPoint point)
    {
        if (point != _point)
        {
            return;
        }

        // Written whole, then renamed, so a test that sees the file never reads half of it.
        var staging = Path.Join(_home, HeldFileName + ".tmp");
        File.WriteAllText(staging, point.ToString());
        File.Move(staging, Path.Join(_home, HeldFileName), overwrite: true);
        Thread.Sleep(Timeout.Infinite);
    }
}
