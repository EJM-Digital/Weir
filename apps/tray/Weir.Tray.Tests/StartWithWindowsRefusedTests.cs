using System.Security.AccessControl;

using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// A registry Windows will not let the account read or change never stops Weir: the installer's hooks log it and go on, and the
/// person's answer is not recorded when it could not be carried out.
/// </summary>
public sealed class StartWithWindowsRefusedTests : IDisposable
{
    private const string Executable = @"C:\Users\someone\AppData\Local\Weir\current\Weir.exe";

    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly TempDirectory _startupFolder = TempDirectory.Create();
    private readonly TempRegistryKey _run = TempRegistryKey.Under(@"Software\WeirTrayTests");
    private readonly TempRegistryKey _answer = TempRegistryKey.Under(@"Software\WeirTrayTests");
    private readonly List<IDisposable> _refusals = [];

    public void Dispose()
    {
        foreach (var refusal in _refusals)
        {
            refusal.Dispose();
        }

        _answer.Dispose();
        _run.Dispose();
        _startupFolder.Dispose();
        _home.Dispose();
    }

    private string Log => LogFile.ReadAllText(Path.Combine(_home.Path, TrayLog.FileName));

    private StartupRegistration Registration() => new(_run.Path, new StartWithWindowsAnswer(_answer.Path), _startupFolder.Path, Executable);

    private void Refuse(string path, RegistryRights rights) => _refusals.Add(new RefusedRegistryKey(path, rights));

    [Fact]
    public void A_run_key_that_cannot_be_read_says_so_and_counts_as_off()
    {
        Refuse(_run.Path, RegistryRights.QueryValues);

        Assert.False(Registration().IsEnabled);

        Assert.Contains("Could not read whether Weir starts with Windows:", Log, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_key_that_cannot_be_written_is_not_turned_on_and_the_answer_is_not_recorded()
    {
        Refuse(_run.Path, RegistryRights.SetValue | RegistryRights.CreateSubKey);

        var registration = Registration();
        registration.Choose(startWithWindows: true);

        Assert.False(registration.IsAnswered);
        Assert.Contains("Could not set Weir to start with Windows:", Log, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_key_that_cannot_be_changed_is_not_turned_off_and_the_answer_is_not_recorded()
    {
        Refuse(_run.Path, RegistryRights.SetValue | RegistryRights.Delete);

        var registration = Registration();
        registration.Choose(startWithWindows: false);

        Assert.False(registration.IsAnswered);
        Assert.Contains("Could not stop Weir starting with Windows:", Log, StringComparison.Ordinal);
    }

    [Fact]
    public void Turning_it_off_removes_a_startup_folder_shortcut_too()
    {
        var shortcut = Path.Combine(_startupFolder.Path, "Weir.lnk");
        File.WriteAllText(shortcut, "x");

        Assert.True(Registration().Disable());

        Assert.False(File.Exists(shortcut));
        Assert.Contains("Removed startup folder shortcut.", Log, StringComparison.Ordinal);
    }

    [Fact]
    public void An_answer_key_that_cannot_be_read_counts_as_not_answered()
    {
        Refuse(_answer.Path, RegistryRights.QueryValues);

        Assert.False(new StartWithWindowsAnswer(_answer.Path).IsRecorded);

        Assert.Contains("Could not read whether the start-with-Windows question was answered:", Log, StringComparison.Ordinal);
    }

    [Fact]
    public void An_answer_that_cannot_be_written_is_logged_and_left_unrecorded()
    {
        Refuse(_answer.Path, RegistryRights.SetValue | RegistryRights.CreateSubKey);
        var answer = new StartWithWindowsAnswer(_answer.Path);

        answer.Record();

        Assert.Contains("Could not record the answer to the start-with-Windows question:", Log, StringComparison.Ordinal);
        Assert.False(answer.IsRecorded);
    }

    [Fact]
    public void An_answer_that_cannot_be_forgotten_is_logged()
    {
        new StartWithWindowsAnswer(_answer.Path).Record();
        Refuse(_answer.Path, RegistryRights.SetValue | RegistryRights.Delete);

        new StartWithWindowsAnswer(_answer.Path).Forget();

        Assert.Contains("Could not forget the answer to the start-with-Windows question:", Log, StringComparison.Ordinal);
    }
}
