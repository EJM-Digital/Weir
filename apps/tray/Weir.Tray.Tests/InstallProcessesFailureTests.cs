using System.ComponentModel;
using System.Diagnostics;

using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// What the install's process clean-up does when Windows will not answer: a path that cannot be read, a process that cannot be
/// killed or waited for, a session that cannot be named. Each is logged and the process is left alone, never guessed at.
/// </summary>
public sealed class InstallProcessesFailureTests : IDisposable
{
    private const string Root = @"C:\Weir\current";

    private readonly TempDirectory _root = TempDirectory.Create();
    private readonly StandInServers _servers = new();
    private readonly Process _self = Process.GetCurrentProcess();
    private readonly List<string> _log = [];

    public void Dispose()
    {
        _servers.Dispose();
        _root.Dispose();
        _self.Dispose();
    }

    [Fact]
    public void A_path_that_cannot_be_normalized_is_not_inside_the_install()
    {
        Assert.False(InstallProcesses.IsInside("C:\\Weir\\current\\bad\0name.exe", Root));
    }

    [Fact]
    public void A_process_that_has_no_main_module_has_no_path()
    {
        Assert.Null(InstallProcesses.PathOf(null));
    }

    [Fact]
    public void A_process_with_a_main_module_has_its_path()
    {
        Assert.Equal(_self.MainModule!.FileName, InstallProcesses.PathOf(_self.MainModule));
    }

    [Fact]
    public void A_process_that_cannot_be_waited_for_is_reported_as_still_running()
    {
        using var notStarted = new Process();

        Assert.False(InstallProcesses.ExitsWithin(notStarted, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(Win32Exception))]
    [InlineData(typeof(NotSupportedException))]
    public void A_process_that_cannot_be_killed_is_logged_and_not_counted_as_stopped(Type failure)
    {
        var own = new InstallProcesses.OwnProcess(_self, "WeirServer", @"C:\Weir\current\server\WeirServer.exe");

        var stopped = InstallProcesses.TryKill(own, _ => throw (Exception)Activator.CreateInstance(failure, "Access is denied.")!, TimeSpan.Zero, _log.Add, "update");

        Assert.False(stopped);
        Assert.Equal([$"update: could not stop WeirServer (pid {_self.Id}) at C:\\Weir\\current\\server\\WeirServer.exe: Access is denied."], _log);
    }

    [Fact]
    public void A_process_that_was_killed_is_logged_as_stopped()
    {
        var own = new InstallProcesses.OwnProcess(_self, "Weir", @"C:\Weir\current\Weir.exe");

        var stopped = InstallProcesses.TryKill(own, _ => { }, TimeSpan.Zero, _log.Add, "update");

        Assert.True(stopped);
        Assert.Equal([$"update: stopped Weir (pid {_self.Id}) at C:\\Weir\\current\\Weir.exe."], _log);
    }

    [Fact]
    public void This_process_is_never_one_of_its_own()
    {
        var own = InstallProcesses.IsOwn(_self, "Weir", _self.Id, session: null, Root, _log.Add, "update", out var executable, _ => @"C:\Weir\current\Weir.exe");

        Assert.False(own);
        Assert.Equal(string.Empty, executable);
        Assert.Empty(_log);
    }

    [Fact]
    public void A_process_in_another_windows_session_is_left_out_without_a_word()
    {
        var own = InstallProcesses.IsOwn(_self, "Weir", self: -1, session: _self.SessionId + 1, Root, _log.Add, "update", out _, _ => @"C:\Weir\current\Weir.exe");

        Assert.False(own);
        Assert.Empty(_log);
    }

    [Fact]
    public void A_process_in_this_windows_session_inside_the_install_is_its_own()
    {
        var own = InstallProcesses.IsOwn(_self, "Weir", self: -1, session: _self.SessionId, Root, _log.Add, "update", out var executable, _ => @"C:\Weir\current\Weir.exe");

        Assert.True(own);
        Assert.Equal(@"C:\Weir\current\Weir.exe", executable);
    }

    [Fact]
    public void A_process_whose_path_cannot_be_read_is_left_running_and_the_log_says_why()
    {
        var own = InstallProcesses.IsOwn(_self, "Weir", self: -1, session: null, Root, _log.Add, "update", out _, _ => throw new Win32Exception("Access is denied."));

        Assert.False(own);
        Assert.Equal([$"update: left Weir (pid {_self.Id}) running: could not read where it runs from (Access is denied.)."], _log);
    }

    [Fact]
    public void A_process_with_no_path_is_left_running_and_the_log_says_the_path_is_unknown()
    {
        var own = InstallProcesses.IsOwn(_self, "Weir", self: -1, session: null, Root, _log.Add, "update", out _, _ => null);

        Assert.False(own);
        Assert.Equal([$@"update: left Weir (pid {_self.Id}) running: unknown path is not part of this install ({Root})."], _log);
    }

    [Fact]
    public void When_this_windows_session_cannot_be_read_nothing_is_stopped_and_the_log_says_why()
    {
        var found = InstallProcesses.FindOwn(Root, sameSessionOnly: true, _log.Add, "update", () => throw new InvalidOperationException("No session."));

        Assert.Empty(found);
        Assert.Equal(["update: stopped nothing: could not read this process's Windows session (No session.)."], _log);
    }

    [Fact]
    public async Task Servers_that_ignore_the_request_share_one_wait_so_a_later_one_is_killed_without_waiting_again()
    {
        var install = Path.Combine(_root.Path, "install", "current");
        var first = await _servers.StartAsync(Path.Combine(install, "first"), "ignore-stop-requests");
        var second = await _servers.StartAsync(Path.Combine(install, "second"), "ignore-stop-requests");
        var spent = new ServerStopTimeouts(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));

        var stopped = InstallProcesses.StopOwn(install, sameSessionOnly: false, _log.Add, "test", spent);

        Assert.Contains(first.Id, stopped);
        Assert.Contains(second.Id, stopped);
        Assert.True(first.WaitForExit(5_000), "the first server should have been killed");
        Assert.True(second.WaitForExit(5_000), "the second server should have been killed");
    }

    [Fact]
    public void A_wait_that_has_run_its_course_has_nothing_left_and_one_that_has_not_has_the_rest()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), InstallProcesses.Remaining(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(7)));
        Assert.Equal(TimeSpan.Zero, InstallProcesses.Remaining(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.Zero, InstallProcesses.Remaining(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12)));
    }
}
