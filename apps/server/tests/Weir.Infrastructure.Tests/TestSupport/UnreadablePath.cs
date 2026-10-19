using System.Security.AccessControl;
using System.Security.Principal;

namespace Weir.Infrastructure.Tests;

/// <summary>
/// A real folder this process may not list, or file it may not read: the current user is denied the right on Windows, and the permission bits are
/// cleared elsewhere. Disposing restores access so the temp directory can be removed.
/// </summary>
internal sealed class UnreadablePath : IDisposable
{
    private readonly string _path;
    private readonly FileSystemAccessRule? _rule;

    private UnreadablePath(string path, FileSystemAccessRule? rule)
    {
        _path = path;
        _rule = rule;
    }

    /// <summary>The folder made unreadable, or null when the process is privileged and so cannot be refused (the test then has nothing to show).</summary>
    public static UnreadablePath? Create(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // ListDirectory and ReadData are the same right, named for what it means on a folder and on a file.
            var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
            if (Directory.Exists(path))
            {
                var folder = new DirectoryInfo(path);
                var folderSecurity = folder.GetAccessControl();
                folderSecurity.AddAccessRule(rule);
                folder.SetAccessControl(folderSecurity);
            }
            else
            {
                var file = new FileInfo(path);
                var fileSecurity = file.GetAccessControl();
                fileSecurity.AddAccessRule(rule);
                file.SetAccessControl(fileSecurity);
            }

            return new UnreadablePath(path, rule);
        }

        if (Environment.IsPrivilegedProcess)
        {
            return null;
        }

        File.SetUnixFileMode(path, UnixFileMode.None);
        return new UnreadablePath(path, null);
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            if (Directory.Exists(_path))
            {
                var folder = new DirectoryInfo(_path);
                var folderSecurity = folder.GetAccessControl();
                folderSecurity.RemoveAccessRule(_rule!);
                folder.SetAccessControl(folderSecurity);
            }
            else
            {
                var file = new FileInfo(_path);
                var fileSecurity = file.GetAccessControl();
                fileSecurity.RemoveAccessRule(_rule!);
                file.SetAccessControl(fileSecurity);
            }

            return;
        }

        File.SetUnixFileMode(_path, Directory.Exists(_path)
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
