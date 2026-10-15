using Weir.Core.Json;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Jobs;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>A pass is logged against the workflow that ran it, even when another workflow holds a file at the same path.</summary>
public sealed class FileLogWorkflowTests : IDisposable
{
    private const string Path = "Heat (1995)/heat.mkv";
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly JobsTestDatabase _db = new();

    public FileLogWorkflowTests()
    {
        _db.Execute("INSERT INTO libraries (id, name, media_type, watched_folder, display_order) VALUES (1, 'Movies', 'movie', '/in', 1), (2, 'Kids', 'movie', '/kids', 2)");
        _db.Execute($"INSERT INTO files (id, library_id, relative_path) VALUES (10, 1, '{Path}'), (20, 2, '{Path}')");
    }

    public void Dispose() => _db.Dispose();

    private async Task RecordAsync(WireObject detail)
    {
        var uow = await UnitOfWork.OpenAsync(_db.Database);
        await using (uow.ConfigureAwait(false))
        {
            await RemuxPassFileState.RecordFileLogAsync(uow, Path, "Pass finished", detail, Now);
            await uow.CommitAsync();
        }
    }

    [Fact]
    public async Task A_pass_of_the_second_workflow_is_logged_against_the_second_workflow_and_its_file()
    {
        await RecordAsync(new WireObject().Set("outcome", "processed").Set("library_id", 2L));

        Assert.Equal("2|Kids|20", _db.Scalar("SELECT library_id || '|' || library_name || '|' || file_id FROM file_logs")?.ToString());
    }

    [Fact]
    public async Task A_pass_of_the_first_workflow_is_logged_against_the_first_workflow_and_its_file()
    {
        await RecordAsync(new WireObject().Set("outcome", "processed").Set("library_id", 1L));

        Assert.Equal("1|Movies|10", _db.Scalar("SELECT library_id || '|' || library_name || '|' || file_id FROM file_logs")?.ToString());
    }
}
