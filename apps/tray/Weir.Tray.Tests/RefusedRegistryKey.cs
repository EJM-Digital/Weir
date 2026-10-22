using System.Security.AccessControl;
using System.Security.Principal;

using Microsoft.Win32;

namespace Weir.Tray.Tests;

/// <summary>
/// A key under HKCU that Windows refuses this account some rights on, so the code that meets the refusal meets the real one.
/// The refusal is lifted on dispose, so the temporary key can be deleted. A volatile key is one the registry forgets at sign-out.
/// </summary>
internal sealed class RefusedRegistryKey : IDisposable
{
    private readonly string _path;
    private readonly RegistryAccessRule _refusal;

    internal RefusedRegistryKey(string path, RegistryRights refused, bool isVolatile = false)
    {
        _path = path;
        _refusal = new RegistryAccessRule(WindowsIdentity.GetCurrent().User!, refused, AccessControlType.Deny);
        Registry.CurrentUser.CreateSubKey(path, RegistryKeyPermissionCheck.Default, isVolatile ? RegistryOptions.Volatile : RegistryOptions.None).Dispose();
        Change(security => security.AddAccessRule(_refusal));
    }

    public void Dispose() => Change(security => security.RemoveAccessRuleSpecific(_refusal));

    private void Change(Action<RegistrySecurity> change)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadPermissions)!;
        var security = key.GetAccessControl();
        change(security);
        key.SetAccessControl(security);
    }
}
