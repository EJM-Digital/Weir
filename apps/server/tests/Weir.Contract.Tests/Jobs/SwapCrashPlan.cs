namespace Weir.Contract.Tests.Jobs;

/// <summary>
/// What a server stopped at each point of a swap leaves on disk, and what is left once Weir has started again. The film is
/// <c>Film (2020)/Film (2020).mkv</c> in the library folder; the originals folder is the default one inside it.
/// </summary>
internal static class SwapCrashPlan
{
    private const string Film = "Film (2020)/Film (2020).mkv";
    private const string Backup = "Film (2020)/Film (2020).weir-bak.mkv";
    private const string Copy = "Film (2020)/Film (2020).weir-tmp.mkv";
    private const string Kept = ".weir-originals/Film (2020)/Film (2020).mkv";

    /// <summary>Every point of a swap, in the order it passes them.</summary>
    public static readonly string[] Points =
    [
        "Started",
        "CopyChecked",
        "CommitRecorded",
        "OriginalMovedAside",
        "CleanedCopyInPlace",
        "CommittedRecorded",
        "BackupDealtWith",
        "FinishedRecorded",
    ];

    /// <summary>The points before the cleaned copy takes the original's name, where the original is what must be left.</summary>
    public static readonly string[] BeforeTheCommit = [.. Points.Take(Array.IndexOf(Points, "CleanedCopyInPlace"))];

    /// <summary>The rows of a test that runs every point, once with the original kept and once without.</summary>
    public static IEnumerable<object[]> EveryPoint(bool keepOriginal) => Points.Select(point => new object[] { point, keepOriginal });

    public static bool Commits(string point) => Array.IndexOf(Points, point) >= Array.IndexOf(Points, "CleanedCopyInPlace");

    /// <summary>The files under the library folder at the moment the server stopped.</summary>
    public static string[] FilesWhenStopped(string point, bool keepOriginal)
    {
        var reservation = keepOriginal ? new[] { Kept } : [];
        return Sorted(point switch
        {
            "Started" => [Film],
            "CopyChecked" => [Film, Copy],
            "CommitRecorded" => [Film, Copy, .. reservation],
            "OriginalMovedAside" => [Backup, Copy, .. reservation],
            "CleanedCopyInPlace" or "CommittedRecorded" => [Film, Backup, .. reservation],
            "BackupDealtWith" or "FinishedRecorded" => [Film, .. reservation],
            _ => throw new ArgumentOutOfRangeException(nameof(point), point, "Not a point of a swap."),
        });
    }

    /// <summary>The files under the library folder once the server has started again: the film, and its kept original when it was cleaned.</summary>
    public static string[] FilesAfterRecovery(string point, bool keepOriginal) =>
        Sorted(keepOriginal && Commits(point) ? [Film, Kept] : [Film]);

    /// <summary>The state the swap's record is left in once Weir has started again and looked at it.</summary>
    public static string RecordAfterRecovery(string point) => point == "FinishedRecorded" ? "finished" : "recovered";

    private static string[] Sorted(IEnumerable<string> files) => [.. files.Order(StringComparer.Ordinal)];
}
