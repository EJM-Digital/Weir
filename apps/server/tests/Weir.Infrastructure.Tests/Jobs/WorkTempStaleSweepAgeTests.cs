using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Jobs;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>
/// How old Weir's own left-behind work file must be before the sweep removes it is the environment's number, not a constant: a file the
/// default (a day) would keep goes when <c>WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_MIN_STALE_AGE_SECONDS</c> is shorter than its age, and a
/// file the default would remove stays when it is longer.
/// </summary>
public sealed class WorkTempStaleSweepAgeTests : IDisposable
{
    private const string Variable = "WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_MIN_STALE_AGE_SECONDS";

    private StoreFixture? _store;

    public void Dispose() => _store?.Dispose();

    private async Task<bool> RemovedAsync(string? environmentSeconds, TimeSpan fileAge)
    {
        _store = environmentSeconds is null ? new StoreFixture() : new StoreFixture((Variable, environmentSeconds));
        _store.Clock.Set(DateTimeOffset.UtcNow);
        var folder = ProcessingLibraryFolders.DefaultMovieWorkFolder(_store.Options.WeirHome);
        Directory.CreateDirectory(folder);
        var path = Path.Join(folder, "Paper.Lanterns.2023.processing.ab12cd34.mkv");
        File.WriteAllText(path, "partial remux output");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - fileAge);
        var handler = new WorkTempStaleSweepHandler(
            new ProcessingJobStore(_store.Database, _store.Clock), _store.Options, _store.Clock, NullLogger<WorkTempStaleSweepHandler>.Instance);

        await handler.HandleAsync(
            new JobWorkContext(1, PeriodicJobKinds.WorkTempStaleSweep, """{"media_scope": "movie", "trigger": "scheduled"}""", "test"),
            CancellationToken.None);

        return !File.Exists(path);
    }

    [Theory]
    [InlineData(null, 5, false)]
    [InlineData(null, 20 * 60, false)]
    [InlineData(null, 30 * 60, true)]
    [InlineData("120", 5, true)]
    [InlineData("120", 1, false)]
    [InlineData("3600", 30, false)]
    [InlineData("3600", 90, true)]
    public async Task A_work_file_goes_when_it_is_older_than_the_age_the_environment_names(string? environmentSeconds, int fileAgeMinutes, bool removed)
    {
        Assert.Equal(removed, await RemovedAsync(environmentSeconds, TimeSpan.FromMinutes(fileAgeMinutes)));
    }
}
