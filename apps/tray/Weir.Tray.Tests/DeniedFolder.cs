using System.Security.AccessControl;
using System.Security.Principal;

namespace Weir.Tray.Tests;

/// <summary>
/// A folder Windows refuses this account some rights on, so the code that meets the refusal meets the real one. The refusal is
/// lifted on dispose, so the folder can be deleted.
/// </summary>
internal sealed class DeniedFolder : IDisposable
{
    private readonly DirectoryInfo _folder;
    private readonly FileSystemAccessRule _refusal;

    internal DeniedFolder(string path, FileSystemRights refused)
    {
        _folder = Directory.CreateDirectory(path);
        _refusal = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, refused, AccessControlType.Deny);
        var security = _folder.GetAccessControl();
        security.AddAccessRule(_refusal);
        _folder.SetAccessControl(security);
    }

    public void Dispose()
    {
        var security = _folder.GetAccessControl();
        security.RemoveAccessRuleSpecific(_refusal);
        _folder.SetAccessControl(security);
    }
}
