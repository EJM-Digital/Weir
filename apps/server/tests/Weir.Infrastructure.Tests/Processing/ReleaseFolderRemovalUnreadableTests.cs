using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Weir.Infrastructure.Processing;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>
/// A release folder with a subfolder the operating system will not let Weir read. The video inside it cannot be seen, so the
/// folder must never be taken for one that holds nothing else: only the film goes, never the folder and what is in it.
/// </summary>
public sealed class ReleaseFolderRemovalUnreadableTests
{
    [Fact]
    public void An_unreadable_subfolder_is_an_error_not_a_folder_with_nothing_in_it()
    {
        using var temp = new TempDirectory();
        var (film, locked) = Layout(temp);

        using (Unreadable(locked))
        {
            if (CanStillList(locked))
            {
                return; // Running as a user the permission does not bind (root): there is no unreadable folder to test with.
            }

            Assert.Throws<UnauthorizedAccessException>(() => HoldsOthers(film));
        }
    }

    [Fact]
    public void A_release_with_an_unreadable_subfolder_loses_only_its_film()
    {
        using var temp = new TempDirectory();
        var (film, locked) = Layout(temp);
        var root = temp.Join("watched");

        ReleaseRemoval removal;
        using (Unreadable(locked))
        {
            if (CanStillList(locked))
            {
                return;
            }

            removal = ReleaseFolderRemoval.Remove(root, film, null);
        }

        Assert.Equal(ReleaseRemovalKind.FileOnly, removal.Kind);
        Assert.Equal(ReleaseFolderRemoval.UnreadableFolderReason, removal.Reason);
        Assert.False(File.Exists(film));
        Assert.True(File.Exists(Path.Join(locked, "Two.mkv")));
    }

    private static (string Film, string Locked) Layout(TempDirectory temp)
    {
        var pack = Directory.CreateDirectory(temp.Join("watched", "Pack")).FullName;
        var locked = Directory.CreateDirectory(Path.Join(pack, "Bonus")).FullName;
        var film = Path.Join(pack, "One.mkv");
        File.WriteAllBytes(film, new byte[16]);
        File.WriteAllBytes(Path.Join(locked, "Two.mkv"), new byte[16]);
        return (film, locked);
    }

    private static bool HoldsOthers(string film) =>
        ReleaseFolderRemoval.HoldsOtherVideos(Path.GetDirectoryName(film)!, film, ReleaseFolderRemoval.VideoExtensions(null));

    private static bool CanStillList(string folder)
    {
        try
        {
            _ = Directory.GetFileSystemEntries(folder);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IDisposable Unreadable(string folder) => OperatingSystem.IsWindows() ? new WindowsDeny(folder) : new UnixDeny(folder);

    [SupportedOSPlatform("windows")]
    private sealed class WindowsDeny : IDisposable
    {
        private readonly DirectoryInfo _folder;
        private readonly FileSystemAccessRule _rule;

        public WindowsDeny(string folder)
        {
            _folder = new DirectoryInfo(folder);
            _rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
            var security = _folder.GetAccessControl();
            security.AddAccessRule(_rule);
            _folder.SetAccessControl(security);
        }

        public void Dispose()
        {
            var security = _folder.GetAccessControl();
            security.RemoveAccessRule(_rule);
            _folder.SetAccessControl(security);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private sealed class UnixDeny : IDisposable
    {
        private readonly string _folder;

        public UnixDeny(string folder)
        {
            _folder = folder;
            File.SetUnixFileMode(folder, UnixFileMode.None);
        }

        public void Dispose() => File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
