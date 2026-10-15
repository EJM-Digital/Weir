using FsCheck;
using FsCheck.Fluent;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;
using Weir.TestSupport;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// A hand-off of a release folder (<see cref="MediaManagerIntake.HandoffMediaFiles"/>) on a real folder of any shape: which files in it are
/// the videos Weir takes, and a hand-off that names one file or nothing that is there.
/// </summary>
public sealed class HandoffReleaseFolderPropertyTests : IDisposable
{
    private const int FileRuns = 60;

    private static readonly string[] Folders = ["Extras", "Sample", "Disc 1", "Subs", "Featurettes", "samples"];

    private static readonly string[] Names = ["Film", "Sample", "sample", "Movie.Sample", "Trailer", "Part 1", "Part 2", "Film.2001", "SAMPLE-clip", "resample"];

    private static readonly string[] Extensions = [".mkv", ".mp4", ".nfo", ".srt", ".jpg", ".txt"];

    private static readonly Gen<IReadOnlyList<string>> FilesInTheRelease =
        from count in Gen.Choose(0, 8)
        from files in Gen.ArrayOf(
            from depth in Gen.Choose(0, 2)
            from folders in Gen.ArrayOf(Gen.Elements(Folders), depth)
            from name in Gen.Elements(Names)
            from extension in Gen.Elements(Extensions)
            select string.Join('/', folders.Append(name + extension)),
            count)
        select (IReadOnlyList<string>)files.DistinctBy(file => file.ToLowerInvariant()).ToList();

    private readonly TempDirectory _watched = new();

    public void Dispose() => _watched.Dispose();

    [Fact]
    public void A_release_folder_means_its_main_videos_or_all_of_them_when_every_video_is_a_sample_and_it_is_refused_when_it_holds_none()
    {
        PropertyChecks.ForAll(FilesInTheRelease, files =>
        {
            var release = $"Release-{Guid.NewGuid():N}";
            var folder = Path.Join(_watched.Path, release);
            foreach (var file in files)
            {
                var path = Path.Join(folder, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "x");
            }

            Directory.CreateDirectory(folder);
            var library = new IntakeLibrary(1, "movie", _watched.Path);
            var videos = files.Where(file => file.EndsWith(".mkv", StringComparison.Ordinal) || file.EndsWith(".mp4", StringComparison.Ordinal)).ToList();
            var main = videos.Where(file => !IsSample(file)).ToList();
            var meant = main.Count > 0 ? main : videos;

            if (meant.Count == 0)
            {
                var refusal = Assert.Throws<IntakeRefusedException>(() => MediaManagerIntake.HandoffMediaFiles(library, release));
                Assert.Equal(400, refusal.StatusCode);
                return;
            }

            var chosen = MediaManagerIntake.HandoffMediaFiles(library, release);
            Assert.Equal(
                meant.Select(file => $"{release}/{file}").Order(StringComparer.OrdinalIgnoreCase),
                chosen.Order(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            Assert.Equal(chosen.Count, chosen.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            for (var i = 1; i < chosen.Count; i++)
            {
                Assert.True(IntakeRules.ComparePathParts(chosen[i - 1].Split('/'), chosen[i].Split('/'), OperatingSystem.IsWindows()) <= 0);
            }
        }, FileRuns);
    }

    [Fact]
    public void A_hand_off_of_one_file_or_of_something_that_is_not_there_means_just_that_path()
    {
        var library = new IntakeLibrary(1, "movie", _watched.Path);
        var absent = from release in Gen.Elements("Release", "Release/Film.mkv", "Gone/Film.mkv")
                     from extra in Gen.Elements(string.Empty, "x")
                     select release + extra;
        File.WriteAllText(_watched.Join("single.mkv"), "x");

        PropertyChecks.ForAll(absent, relative =>
        {
            Assert.Equal([relative], MediaManagerIntake.HandoffMediaFiles(library, relative));
            Assert.Equal(["single.mkv"], MediaManagerIntake.HandoffMediaFiles(library, "single.mkv"));
            Assert.Equal([relative], MediaManagerIntake.HandoffMediaFiles(null, relative));
        }, FileRuns);
    }

    private static bool IsSample(string path) =>
        path.Split('/').Any(part => part.Split([.. Enumerable.Range(0, 128).Select(code => (char)code).Where(character => !char.IsAsciiLetterOrDigit(character))])
            .Any(token => token.Equals("sample", StringComparison.OrdinalIgnoreCase)));
}
