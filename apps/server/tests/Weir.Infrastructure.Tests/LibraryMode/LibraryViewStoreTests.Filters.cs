using Weir.Core.LibraryMode;
using Weir.Infrastructure.LibraryMode;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>The Files listing narrowed by class, manager, what Weir has done with the file, and facets it is sent that it does not know.</summary>
public sealed partial class LibraryViewStoreTests
{
    private async Task<string[]> PathsAsync(LibraryFileQuery query) =>
        [.. (await Read(uow => _libraryView.ListFilesAsync(uow, _libraryId, query))).Select(file => file.Path).Order(StringComparer.Ordinal)];

    private async Task ThreeFilesAsync()
    {
        await LibraryAsync();
        await RecordAsync(
            File("/lib/a.mkv", LibraryFileClassification.Matches, Probe("h264", 1080, ("audio", "aac", 2, "eng")), managerKind: "Sonarr"),
            File("/lib/b.mkv", LibraryFileClassification.WouldChange, Probe("hevc", 2160, ("audio", "eac3", 6, "eng")), managerKind: "radarr"),
            File("/lib/c.mkv", LibraryFileClassification.CannotProcess, "", problem: LibraryProblemKind.Unreadable));
    }

    [Fact]
    public async Task Files_can_be_narrowed_to_one_class()
    {
        await ThreeFilesAsync();

        Assert.Equal(["/lib/b.mkv"], await PathsAsync(new LibraryFileQuery { Classification = "would_change" }));
        Assert.Equal(["/lib/a.mkv"], await PathsAsync(new LibraryFileQuery { Classification = "matches" }));
    }

    [Fact]
    public async Task Files_can_be_narrowed_to_one_manager_whatever_the_case()
    {
        await ThreeFilesAsync();

        Assert.Equal(["/lib/a.mkv"], await PathsAsync(new LibraryFileQuery { ManagerKind = "SONARR" }));
        Assert.Equal(["/lib/b.mkv"], await PathsAsync(new LibraryFileQuery { ManagerKind = "Radarr" }));
    }

    [Fact]
    public async Task Files_can_be_narrowed_to_those_cleaned_or_left_alone()
    {
        await ThreeFilesAsync();
        await _store.Execute(
            $"INSERT INTO library_file_marks (library_id, path, cleaned_at, updated_at) VALUES ({_libraryId}, '/lib/a.mkv', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)");
        await _store.Execute(
            $"INSERT INTO library_file_marks (library_id, path, leave_alone, updated_at) VALUES ({_libraryId}, '/lib/b.mkv', 1, CURRENT_TIMESTAMP)");

        Assert.Equal(["/lib/a.mkv"], await PathsAsync(new LibraryFileQuery { State = "cleaned" }));
        Assert.Equal(["/lib/b.mkv"], await PathsAsync(new LibraryFileQuery { State = "left_alone" }));
        Assert.Equal(3, (await PathsAsync(new LibraryFileQuery { State = "anything else" })).Length);
    }

    [Fact]
    public async Task Facets_that_are_unknown_or_empty_narrow_nothing()
    {
        await ThreeFilesAsync();

        var paths = await PathsAsync(new LibraryFileQuery
        {
            Facets = [new LibraryFileFacet("not-a-facet", "x"), new LibraryFileFacet(LibraryFacets.AudioLanguage, string.Empty)],
        });

        Assert.Equal(3, paths.Length);
    }

    [Fact]
    public async Task A_blank_class_manager_or_search_narrows_nothing()
    {
        await ThreeFilesAsync();

        var paths = await PathsAsync(new LibraryFileQuery { Classification = " ", ManagerKind = " ", Search = " " });

        Assert.Equal(3, paths.Length);
    }
}
