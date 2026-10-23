using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Jobs;

public sealed class CompletedMovieRemovalTests
{
    [Fact]
    public async Task Marking_a_removed_release_folders_other_files_processed_tells_the_lists_that_show_them()
    {
        using var fixture = new StoreFixture();
        var library = await fixture.Scalar("INSERT INTO libraries (name, media_type) VALUES ('Films', 'movie') RETURNING id");
        await fixture.Execute(
            "INSERT INTO files (library_id, relative_path, status, last_seen_at) VALUES " +
            $"({library}, 'Film/film.mkv', 'processed', CURRENT_TIMESTAMP), ({library}, 'Film/Gallery.mkv', 'skipped', CURRENT_TIMESTAMP)");
        var notifier = ActivityNotifications.For(fixture.Database);
        var before = notifier.Snapshot();

        var uow = await UnitOfWork.OpenAsync(fixture.Database);
        await using (uow)
        {
            await CompletedMovieRemoval.MarkReleaseFolderProcessedAsync(uow, library, "Film/film.mkv");
            await uow.CommitAsync();
        }

        Assert.Equal(2, await fixture.Scalar("SELECT count(*) FROM files WHERE status = 'processed'"));
        Assert.True(notifier.Snapshot().Version > before.Version, "No list was told the files changed.");
    }

    [Fact]
    public async Task A_film_directly_in_the_watched_folder_marks_only_the_files_directly_in_it_processed()
    {
        using var fixture = new StoreFixture();
        var library = await fixture.Scalar("INSERT INTO libraries (name, media_type) VALUES ('Films', 'movie') RETURNING id");
        await fixture.Execute(
            "INSERT INTO files (library_id, relative_path, status, last_seen_at) VALUES " +
            $"({library}, 'film.mkv', 'processed', CURRENT_TIMESTAMP), ({library}, 'extra.mkv', 'skipped', CURRENT_TIMESTAMP), ({library}, 'Other/extra.mkv', 'skipped', CURRENT_TIMESTAMP)");

        var uow = await UnitOfWork.OpenAsync(fixture.Database);
        await using (uow)
        {
            await CompletedMovieRemoval.MarkReleaseFolderProcessedAsync(uow, library, "film.mkv");
            await uow.CommitAsync();
        }

        Assert.Equal(2, await fixture.Scalar("SELECT count(*) FROM files WHERE status = 'processed'"));
        Assert.Equal("skipped", await fixture.ScalarText("SELECT status FROM files WHERE relative_path = 'Other/extra.mkv'"));
    }
}
