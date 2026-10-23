using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>A hand-off that lists its files: one that is not there, or goes while the list is checked, refuses the whole hand-off.</summary>
public sealed class HandoffListedFilesTests : IDisposable
{
    private const string Release = "Film (2001)";

    private readonly TempDirectory _dir = new();
    private readonly IntakeLibrary _library;

    public HandoffListedFilesTests()
    {
        _library = new IntakeLibrary(1, "movie", _dir.Join("watch"));
        Directory.CreateDirectory(_library.WatchedFolder);
    }

    public void Dispose() => _dir.Dispose();

    private void Write(string relative)
    {
        var path = Path.Join(_library.WatchedFolder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "film");
    }

    private static MediaManagerImportEvent Event(params string[] listed) => new()
    {
        SourceKey = "deluno",
        EventKind = MediaManagerImportEvent.Handoff,
        MediaScope = "movie",
        FilePath = "/downloads/Film (2001)",
        SourceFiles = listed,
    };

    private static string Listed(string name) => $"/downloads/{Release}/{name}";

    [Fact]
    public void A_listed_file_that_is_not_there_refuses_the_whole_hand_off_and_names_the_file()
    {
        Write($"{Release}/Film.mkv");

        var refusal = Assert.Throws<IntakeRefusedException>(
            () => MediaManagerIntake.HandoffListedFiles(_library, Release, Event(Listed("Film.mkv"), Listed("Gone.mkv"))));

        Assert.Equal(400, refusal.StatusCode);
        Assert.Equal(HandoffSourceFiles.MissingDetail("Gone.mkv"), refusal.Message);
    }

    [Fact]
    public void A_listed_file_that_goes_while_the_list_is_checked_refuses_the_whole_hand_off()
    {
        Write($"{Release}/Film.mkv");
        Write($"{Release}/Extra.mkv");
        var files = new FaultingFileSystem().Before(FileCall.FileExists, _ => File.Delete(Path.Join(_library.WatchedFolder, Release, "Extra.mkv")));

        var refusal = Assert.Throws<IntakeRefusedException>(
            () => MediaManagerIntake.HandoffListedFiles(_library, Release, Event(Listed("Film.mkv"), Listed("Extra.mkv")), files));

        Assert.Equal(HandoffSourceFiles.MissingDetail("Extra.mkv"), refusal.Message);
        Assert.True(File.Exists(Path.Join(_library.WatchedFolder, Release, "Film.mkv")));
    }

    [Fact]
    public void A_listed_file_outside_the_folder_the_hand_off_names_refuses_the_whole_hand_off()
    {
        Write($"{Release}/Film.mkv");
        Write("Other/Other.mkv");

        var refusal = Assert.Throws<IntakeRefusedException>(
            () => MediaManagerIntake.HandoffListedFiles(_library, Release, Event(Listed("Film.mkv"), "/downloads/Other/Other.mkv")));

        Assert.Equal(HandoffSourceFiles.OutsideDetail("Other.mkv"), refusal.Message);
    }

    [Fact]
    public void A_list_of_the_one_file_the_hand_off_names_is_that_file()
    {
        Write("Film.mkv");
        var importEvent = Event("/downloads/Film.mkv") with { FilePath = "/downloads/Film.mkv" };

        Assert.Equal(["Film.mkv"], MediaManagerIntake.HandoffListedFiles(_library, "Film.mkv", importEvent));
    }

    [Fact]
    public void A_list_of_a_video_and_the_files_beside_it_means_only_the_video()
    {
        Write($"{Release}/Film.mkv");
        Write($"{Release}/Film.nfo");
        Write($"{Release}/Other.mkv");

        var chosen = MediaManagerIntake.HandoffListedFiles(_library, Release, Event(Listed("Film.mkv"), Listed("Film.nfo")));

        Assert.Equal([$"{Release}/Film.mkv"], chosen);
    }

    [Fact]
    public void A_list_with_no_video_in_it_refuses_the_hand_off()
    {
        Write($"{Release}/Film.nfo");

        var refusal = Assert.Throws<IntakeRefusedException>(
            () => MediaManagerIntake.HandoffListedFiles(_library, Release, Event(Listed("Film.nfo"))));

        Assert.Equal(HandoffSourceFiles.NoVideoDetail, refusal.Message);
    }

    [Fact]
    public void A_hand_off_that_lists_nothing_is_refused_because_it_names_no_file_of_the_folder()
    {
        var hand_off = Event() with { SourceFiles = null };

        var refusal = Assert.Throws<IntakeRefusedException>(() => MediaManagerIntake.HandoffListedFiles(_library, Release, hand_off));

        Assert.Equal(400, refusal.StatusCode);
    }
}
