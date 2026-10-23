using Weir.Infrastructure.IO;

namespace Weir.Infrastructure.Tests.IO;

/// <summary>What the real file system says about where a path leads.</summary>
public sealed class PhysicalFileSystemLinkTests
{
    [Fact]
    public void A_folder_that_is_not_a_link_leads_nowhere_else()
    {
        using var temp = new TempDirectory();
        var folder = Directory.CreateDirectory(temp.Join("plain")).FullName;

        Assert.Null(PhysicalFileSystem.Instance.ResolveLinkTarget(folder, isDirectory: true, returnFinalTarget: true));
    }

    [Fact]
    public void A_linked_folder_leads_to_the_folder_it_points_at()
    {
        using var temp = new TempDirectory();
        var target = Directory.CreateDirectory(temp.Join("target")).FullName;
        var link = temp.Join("link");
        FolderLinks.Create(link, target);

        Assert.Equal(target, PhysicalFileSystem.Instance.ResolveLinkTarget(link, isDirectory: true, returnFinalTarget: true));
    }
}
