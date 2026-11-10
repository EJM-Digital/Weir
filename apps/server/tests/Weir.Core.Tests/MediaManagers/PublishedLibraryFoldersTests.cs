using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary><see cref="PublishedLibraryFolders"/>: what a media manager reads about each workflow's folders and where it came from.</summary>
public sealed class PublishedLibraryFoldersTests
{
    private static string Dump(PublishedLibraryFolders library) => WireJsonWriter.Dumps(library.ToOut(), WireJsonFormat.Response);

    [Fact]
    public void A_workflow_set_up_from_a_manager_publishes_the_managers_library_id_and_that_it_owns_the_folders()
    {
        var library = new PublishedLibraryFolders(1, "Movies", "movie", "/in", "/work", "/out", "lib-movies", true, 52428800);

        Assert.Equal(
            """{"id":1,"name":"Movies","media_type":"movie","watched_folder":"/in","work_folder":"/work","output_folder":"/out","manager_library_key":"lib-movies","folders_from_manager":true,"minimum_file_size_bytes":52428800}""",
            Dump(library));
    }

    [Fact]
    public void A_workflow_that_is_only_Weirs_publishes_no_key_and_no_manager_ownership()
    {
        var library = new PublishedLibraryFolders(2, "TV", "tv", "/in", "/work", "/out", null, false, null);

        Assert.Equal(
            """{"id":2,"name":"TV","media_type":"tv","watched_folder":"/in","work_folder":"/work","output_folder":"/out","manager_library_key":null,"folders_from_manager":false,"minimum_file_size_bytes":null}""",
            Dump(library));
    }

    [Fact]
    public void Every_workflows_folders_are_published_in_one_list()
    {
        PublishedLibraryFolders[] libraries =
        [
            new(1, "Movies", "movie", "/in", "/work", "/out", "lib-movies", true, null),
            new(2, "TV", "tv", "/tv", "/work", "/out", null, false, null),
        ];

        Assert.Equal(
            "{\"libraries\":[" + Dump(libraries[0]) + "," + Dump(libraries[1]) + "]}",
            WireJsonWriter.Dumps(LibraryFolderPublishing.ToOut(libraries), WireJsonFormat.Response));
        Assert.Equal("""{"libraries":[]}""", WireJsonWriter.Dumps(LibraryFolderPublishing.ToOut([]), WireJsonFormat.Response));
    }

    [Fact]
    public void The_minimum_is_published_in_bytes_and_none_when_the_workflow_has_no_minimum()
    {
        Assert.Equal(50L * 1024 * 1024, PublishedLibraryFolders.MinimumBytesOf(50));
        Assert.Null(PublishedLibraryFolders.MinimumBytesOf(0));
    }

    [Fact]
    public void The_key_is_published_only_while_the_workflow_is_set_up_from_a_manager()
    {
        Assert.Equal("lib-movies", PublishedLibraryFolders.ManagerLibraryKeyOf(5, "lib-movies"));
        Assert.Null(PublishedLibraryFolders.ManagerLibraryKeyOf(null, "lib-movies"));
        Assert.Null(PublishedLibraryFolders.ManagerLibraryKeyOf(5, null));
        Assert.Null(PublishedLibraryFolders.ManagerLibraryKeyOf(5, string.Empty));
    }
}
