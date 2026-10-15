using Microsoft.Win32;

namespace Weir.Tray.Tests;

/// <summary>
/// A throw-away key path under HKCU, unique to one test, deleted on dispose along with its parent once nothing else
/// is under it. Only its own key is removed: another test run on the same account (two CI jobs on one PC) may be
/// using a sibling under the same parent at the same time.
/// </summary>
internal sealed class TempRegistryKey : IDisposable
{
    private readonly string _parent;

    private TempRegistryKey(string parent)
    {
        _parent = parent;
        Path = $@"{parent}\{Guid.NewGuid():n}";
    }

    public string Path { get; }

    public static TempRegistryKey Under(string parent) => new(parent);

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(Path, throwOnMissingSubKey: false);
        try
        {
            Registry.CurrentUser.DeleteSubKey(_parent, throwOnMissingSubKey: false);
        }
        catch (InvalidOperationException)
        {
            // Another run still has a key under the parent; the last one out removes it.
        }
    }
}
