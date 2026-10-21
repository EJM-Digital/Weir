using System.ComponentModel;
using System.Diagnostics;

using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// When File Explorer cannot be started, the person is told where the folder is so they can open it themselves, and the log says why.
/// </summary>
public sealed class RuntimeFoldersFailureTests : IDisposable
{
    private readonly TempDirectory _home = TempDirectory.AsWeirHome();

    public void Dispose() => _home.Dispose();

    private string Log => File.ReadAllText(Path.Combine(_home.Path, TrayLog.FileName));

    [Fact]
    public void A_file_Explorer_cannot_select_is_shown_by_opening_its_folder()
    {
        var file = Path.Combine(_home.Path, "tray-host.log");
        File.WriteAllText(file, "x");
        var started = new List<ProcessStartInfo>();

        RuntimeFolders.Reveal(file, info =>
        {
            started.Add(info);
            if (started.Count == 1)
            {
                throw new Win32Exception("Explorer is not available");
            }
        }, tell: message => throw new Xunit.Sdk.XunitException($"The person was told ({message}) though the folder opened."));

        Assert.Equal(["explorer.exe", _home.Path], started.Select(info => info.FileName));
        Assert.Contains($"Could not show {file} in File Explorer: Explorer is not available", Log, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_Explorer_cannot_select_and_whose_folder_will_not_open_tells_the_person_where_it_is()
    {
        var file = Path.Combine(_home.Path, "tray-host.log");
        File.WriteAllText(file, "x");
        var told = new List<string>();

        RuntimeFolders.Reveal(file, _ => throw new Win32Exception("Explorer is not available"), told.Add);

        Assert.Equal($"Weir could not open its data folder. You can open it in File Explorer yourself:\n\n{_home.Path}", Assert.Single(told));
        Assert.Contains($"Could not open the data folder {_home.Path}: Explorer is not available", Log, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_that_cannot_be_created_tells_the_person_where_it_should_be()
    {
        var blocker = Path.Combine(_home.Path, "a-file");
        File.WriteAllText(blocker, "x");
        var folder = Path.Combine(blocker, "logs");
        var told = new List<string>();

        RuntimeFolders.Open(folder, "logs folder", _ => throw new Xunit.Sdk.XunitException("Explorer was started on a folder that is not there."), told.Add);

        Assert.Equal($"Weir could not open its logs folder. You can open it in File Explorer yourself:\n\n{folder}", Assert.Single(told));
        Assert.Contains($"Could not open the logs folder {folder}:", Log, StringComparison.Ordinal);
    }
}
