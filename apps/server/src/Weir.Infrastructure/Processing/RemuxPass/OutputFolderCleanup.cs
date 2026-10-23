using System.Globalization;
using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>
/// Output-folder cleanup after a successful pass: Movies' per-title folder and TV's season folder, each deleted only when every covering manager
/// answered and none keeps a library file inside it, nothing under it is too new, and no other pass is heading there.
/// </summary>
public sealed partial class OutputFolderCleanup
{
    private readonly IPostSuccessCleanupData _data;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly int _movieMinAgeSeconds;
    private readonly int _tvMinAgeSeconds;

    public OutputFolderCleanup(IPostSuccessCleanupData data, TimeProvider time, ILogger logger, int movieMinAgeSeconds, int tvMinAgeSeconds)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _movieMinAgeSeconds = movieMinAgeSeconds;
        _tvMinAgeSeconds = tvMinAgeSeconds;
    }

    /// <summary>A stable posix path for comparing job payloads.</summary>
    public static string NormalizeRelativeForMatch(string rel)
    {
        var parts = WireStrings.Strip(rel ?? string.Empty).Replace('\\', '/').Split('/').Where(part => part is not ("." or "")).ToList();
        if (rel is not null && WireStrings.Strip(rel).Replace('\\', '/').StartsWith('/'))
        {
            return "/" + string.Join('/', parts);
        }

        return string.Join('/', parts);
    }

    /// <summary>Deletes the movie's output folder after a successful pass when every cleanup gate allows it.</summary>
    public async Task RunMovieAsync(WireObject output, ProcessingPathRuntime runtime, string watchedRoot, string source, string? finalOutputFile, string relativeMediaPath, long? currentJobId, string mediaScope, HandoffOrigin? origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(runtime);
        const string Prefix = "movie_output";
        InitFields(output, Prefix, "movie_output_folder_deleted", "movie_output_folder_path", "movie_output_folder_skip_reason", "movie_output_cascade_folders_deleted");
        output.Set("movie_output_dry_run", false);

        if (mediaScope != "movie")
        {
            Skip(output, Prefix, "movie_output_folder_skip_reason", "This cleanup step applies only to Movies. TV output cleanup is separate and was not run here.");
            return;
        }

        var located = LocateTitleFolder(output, runtime, watchedRoot, source, finalOutputFile, "movie_output_folder_skip_reason", Prefix, tv: false);
        if (located is not { } place)
        {
            return;
        }

        output.Set("movie_output_folder_path", place.Folder);
        var relNorm = NormalizeRelativeForMatch(relativeMediaPath);
        if (relNorm.Length > 0 && await MovieJobBlocksAsync(relNorm, currentJobId, cancellationToken).ConfigureAwait(false))
        {
            Skip(output, Prefix, "movie_output_folder_skip_reason",
                "Another Movies video pass is already waiting or running for this same watched file path, " +
                "so output-folder cleanup was skipped to avoid racing another remux.");
            return;
        }

        var minAge = Math.Max(0, _movieMinAgeSeconds);
        var newest = NewestModifiedUnderTree(place.Folder);
        if (newest is null)
        {
            Skip(output, Prefix, "movie_output_folder_skip_reason",
                "Weir could not read file timestamps under the movie output folder, so nothing was removed for safety.");
            return;
        }

        var age = Now() - newest.Value;
        output.Set("movie_output_age_seconds", (long)Math.Max(0.0, age));
        if (age < minAge)
        {
            Skip(output, Prefix, "movie_output_folder_skip_reason",
                $"This movie output folder was changed too recently (everything under it must be at least {minAge.ToString(CultureInfo.InvariantCulture)}s old; " +
                $"newest file age is about {((long)Math.Max(0, age)).ToString(CultureInfo.InvariantCulture)}s).");
            return;
        }

        await DeleteWhenTruthClearsAsync(output, Prefix, "movie_output_folder_skip_reason", "movie_output_folder_deleted", "movie_output_cascade_folders_deleted", "movie", place, "movie output folder", "Movies", origin, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Deletes the episode's output season folder after a successful pass when every cleanup gate allows it.</summary>
    public async Task RunTvAsync(WireObject output, ProcessingPathRuntime runtime, string watchedRoot, string source, string? finalOutputFile, long? currentJobId, string mediaScope, HandoffOrigin? origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(runtime);
        const string Prefix = "tv_output";
        InitFields(output, Prefix, "tv_output_season_folder_deleted", "tv_output_season_folder_path", "tv_output_season_folder_skip_reason", "tv_output_cascade_folders_deleted");
        output.Set("tv_output_dry_run", false);

        if (mediaScope != "tv")
        {
            Skip(output, Prefix, "tv_output_season_folder_skip_reason", "This cleanup step applies only to TV. Movies output-folder cleanup is separate and was not run here.");
            return;
        }

        var located = LocateTitleFolder(output, runtime, watchedRoot, source, finalOutputFile, "tv_output_season_folder_skip_reason", Prefix, tv: true);
        if (located is not { } place)
        {
            return;
        }

        output.Set("tv_output_season_folder_path", place.Folder);
        if (await TvJobBlocksAsync(place.OutputRoot, place.Folder, currentJobId, cancellationToken).ConfigureAwait(false))
        {
            Skip(output, Prefix, "tv_output_season_folder_skip_reason",
                "Another TV video pass is already waiting or running for an episode whose output maps to this same " +
                "season folder under your TV output folder, so TV output-folder cleanup was skipped to avoid racing another remux.");
            return;
        }

        var episodes = DirectChildMediaCandidates(place.Folder);
        if (episodes.Count == 0)
        {
            Skip(output, Prefix, "tv_output_season_folder_skip_reason",
                "Weir did not find any supported episode media file as a direct child of this season output folder, " +
                "so it could not apply the minimum-age gate for TV output cleanup. The folder was left in place.");
            return;
        }

        var minAge = Math.Max(0, _tvMinAgeSeconds);
        var newest = episodes.Max(episode => Seconds(File.GetLastWriteTimeUtc(episode)));
        var age = Now() - newest;
        output.Set("tv_output_age_seconds", (long)Math.Max(0.0, age));
        if (age < minAge)
        {
            Skip(output, Prefix, "tv_output_season_folder_skip_reason",
                $"Direct-child episode media in this TV season output folder was modified too recently " +
                $"(each must be at least {minAge.ToString(CultureInfo.InvariantCulture)}s old by newest file; newest is about {((long)Math.Max(0, age)).ToString(CultureInfo.InvariantCulture)}s).");
            return;
        }

        await DeleteWhenTruthClearsAsync(output, Prefix, "tv_output_season_folder_skip_reason", "tv_output_season_folder_deleted", "tv_output_cascade_folders_deleted", "tv", place, "TV season output folder", "TV", origin, cancellationToken)
            .ConfigureAwait(false);
    }
}
