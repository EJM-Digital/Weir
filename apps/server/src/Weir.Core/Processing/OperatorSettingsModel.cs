using Weir.Core.MediaManagers;
using Weir.Core.Time;

namespace Weir.Core.Processing;

/// <summary>The singleton <c>operator_settings</c> row (id = 1).</summary>
public sealed record ProcessingOperatorSettingsRecord
{
    public long MaxConcurrentFiles { get; init; } = 1;
    public long RunnerCapacity { get; init; } = 4;
    public long RunnerCostSd { get; init; }
    public long RunnerCost720P { get; init; }
    public long RunnerCost1080P { get; init; } = 1;
    public long RunnerCost4K { get; init; } = 1;

    /// <summary>
    /// Whether the resolution budget (runner capacity and per-resolution costs) also limits what starts (#633). Off, a
    /// file needs only a free slot, so "Files at once" means what it says.
    /// </summary>
    public bool RunnerBudgetEnabled { get; init; }
    public bool WorkTempStaleSweepEnabled { get; init; } = true;

    /// <summary>How often the leftover-work-file sweep runs, set in Setup › Performance › Cleanup; null keeps the environment's interval.</summary>
    public long? WorkTempStaleSweepIntervalSeconds { get; init; }

    /// <summary>
    /// Whether the Cleanup job removes Weir's own hand-back copies nobody claimed (#652). Off until an operator switches
    /// it on, because it deletes files.
    /// </summary>
    public bool UnclaimedHandbackCleanupEnabled { get; init; }

    /// <summary>How many days an unclaimed hand-back copy waits before that job may remove it.</summary>
    public long UnclaimedHandbackWindowDays { get; init; } = 14;

    /// <summary>How often that job runs, set in Setup › Performance › Cleanup; null keeps the built-in six hours.</summary>
    public long? UnclaimedHandbackCleanupIntervalSeconds { get; init; }
    public bool KeepFailedWorkFiles { get; init; }
    public long FileLogRetentionDays { get; init; } = 90;
    public bool MovieScheduleEnabled { get; init; } = true;
    public bool TvScheduleEnabled { get; init; } = true;
    public Timestamp UpdatedAt { get; init; }
}

/// <summary>Clamping and normalization for <c>operator_settings</c>.</summary>
public static class OperatorSettingsRules
{
    /// <summary>The most files Weir runs at once, and so the most worker slots a server starts (#633).</summary>
    public const int MaxFilesAtOnce = 10;

    /// <summary>The shortest interval Setup › Performance › Cleanup offers: a sweep more often than every 15 minutes finds nothing new.</summary>
    public const int MinCleanupIntervalSeconds = 15 * 60;

    /// <summary>The longest interval Setup › Performance › Cleanup offers.</summary>
    public const int MaxCleanupIntervalSeconds = 30 * 24 * 3600;

    /// <summary>A library's own limit meaning "the same as Files at once" (#633).</summary>
    public const long LibraryFollowsFilesAtOnce = 0;

    public static long ClampMaxConcurrentFiles(long raw) => Math.Clamp(raw, 1, MaxFilesAtOnce);

    /// <summary>A library's own limit: 0 follows "Files at once", otherwise 1 to <see cref="MaxFilesAtOnce"/>.</summary>
    public static long ClampLibraryMaxConcurrentFiles(long raw) => Math.Clamp(raw, LibraryFollowsFilesAtOnce, MaxFilesAtOnce);

    /// <summary>What a library's own limit comes to once "the same as Files at once" is resolved. Never above it.</summary>
    public static int EffectiveLibraryLimit(long libraryLimit, long filesAtOnce)
    {
        var global = (int)ClampMaxConcurrentFiles(filesAtOnce);
        return libraryLimit <= LibraryFollowsFilesAtOnce ? global : (int)Math.Min(global, ClampLibraryMaxConcurrentFiles(libraryLimit));
    }

    public static long ClampSizeMb(long raw) => Math.Clamp(raw, 0, 1024 * 1024);

    public static long ClampRunnerCapacity(long raw) => Math.Clamp(raw, 1, 64);

    public static long ClampRunnerCost(long raw) => Math.Clamp(raw, 0, 64);

    public static long ClampFileLogRetentionDays(long raw) => Math.Clamp(raw, 0, 3650);

    /// <summary>The unclaimed hand-back wait, 1 to 365 days.</summary>
    public static long ClampUnclaimedHandbackWindowDays(long raw) =>
        Math.Clamp(raw, HandbackRules.MinUnclaimedWindowDays, HandbackRules.MaxUnclaimedWindowDays);
}
