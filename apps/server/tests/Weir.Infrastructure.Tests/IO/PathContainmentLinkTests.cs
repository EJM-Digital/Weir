using Weir.Infrastructure.IO;

namespace Weir.Infrastructure.Tests.IO;

/// <summary>A path that cannot be followed is either not there, which the folders above decide, or not allowed, which counts as a link.</summary>
public sealed class PathContainmentLinkTests
{
    [Fact]
    public void A_path_that_is_not_there_is_not_a_link()
    {
        using var folder = new TempDirectory();
        var root = folder.Join("root");
        Directory.CreateDirectory(root);

        Assert.False(PathContainment.HasLinkBelowRoot(root, Path.Join(root, "missing", "deeper", "file.mkv")));
    }

    [Fact]
    public void A_path_the_system_refuses_to_describe_counts_as_a_link()
    {
        using var folder = new TempDirectory();
        var root = folder.Join("root");
        Directory.CreateDirectory(root);
        var files = new FaultingFileSystem().Fail(FileCall.GetAttributes, FaultingFileSystem.SharingViolation);

        Assert.True(PathContainment.HasLinkBelowRoot(root, Path.Join(root, "file.mkv"), files));
    }

    [PosixFact("Only permission bits hide a folder's contents on a path the process can still name; Windows denies a listing, not a lookup.")]
    public void A_path_inside_a_folder_that_cannot_be_opened_counts_as_a_link()
    {
        using var folder = new TempDirectory();
        var root = folder.Join("root");
        var locked = Path.Join(root, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Join(locked, "file.mkv"), "x");
        using var denied = UnreadablePath.Create(locked);
        if (denied is null)
        {
            return;
        }

        Assert.True(PathContainment.HasLinkBelowRoot(root, Path.Join(locked, "file.mkv")));
    }
}
