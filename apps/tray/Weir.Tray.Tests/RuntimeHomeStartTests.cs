using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// More than one Windows account on the PC (fast user switching, Remote Desktop): each session has its own single-instance lock and
/// second-launch signal, and an account that finds Weir's data folder made by another account does not use it and says why.
/// </summary>
public sealed class RuntimeHomeStartTests
{
    private const string Home = @"C:\ProgramData\Weir";

    private readonly List<string> _log = [];
    private readonly List<string> _shown = [];

    [Fact]
    public void The_lock_that_keeps_one_tray_to_a_session_and_the_second_launch_signal_are_the_sessions_own()
    {
        Assert.StartsWith(@"Local\", RuntimeHomeStart.Names.SingleInstanceLock, StringComparison.Ordinal);
        Assert.StartsWith(@"Local\", SecondLaunchSignal.DefaultName, StringComparison.Ordinal);
        Assert.NotEqual(RuntimeHomeStart.Names.SingleInstanceLock, SecondLaunchSignal.DefaultName);
    }

    [Fact]
    public void A_second_tray_in_the_same_session_finds_the_lock_taken()
    {
        var name = $"{RuntimeHomeStart.Names.SessionPrefix}WeirTrayTest{Guid.NewGuid():n}";
        using var first = new Mutex(false, name, out var firstCreatedIt);
        using var second = new Mutex(false, name, out var secondCreatedIt);

        Assert.True(firstCreatedIt);
        Assert.False(secondCreatedIt);
    }

    [Fact]
    public void A_folder_this_account_may_use_lets_Weir_start()
    {
        var started = Secure(_ => true, desktop: true);

        Assert.True(started);
        Assert.Single(_log, line => line.StartsWith($"Restricted {Home} to SYSTEM, Administrators and ", StringComparison.Ordinal));
        Assert.Empty(_shown);
    }

    [Fact]
    public void A_folder_another_account_owns_stops_the_start_and_the_person_at_the_desktop_is_told_why()
    {
        var started = Secure(_ => throw new RuntimeHomeOwnedByAnotherAccountException(Home, "PC\\First"), desktop: true);

        Assert.False(started);
        var told = Assert.Single(_shown);
        Assert.Contains("belongs to another Windows account (PC\\First)", told, StringComparison.Ordinal);
        Assert.Contains("take ownership of it", told, StringComparison.Ordinal);
        Assert.Contains("set WEIR_HOME", told, StringComparison.Ordinal);
        Assert.Equal([$"Not starting: {told}"], _log);
    }

    [Fact]
    public void A_folder_another_account_owns_stops_a_start_with_nobody_at_the_desktop_without_showing_anything()
    {
        var started = Secure(_ => throw new RuntimeHomeOwnedByAnotherAccountException(Home, "PC\\First"), desktop: false);

        Assert.False(started);
        Assert.Empty(_shown);
        Assert.Single(_log, line => line.StartsWith("Not starting: ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(IOException))]
    public void A_folder_that_cannot_be_restricted_still_lets_Weir_start_and_says_so(Type failure)
    {
        var started = Secure(_ => throw (Exception)Activator.CreateInstance(failure, "access denied")!, desktop: true);

        Assert.True(started);
        Assert.Empty(_shown);
        Assert.Equal([$"Could not restrict who can read {Home} (access denied); it keeps its current access list."], _log);
    }

    private bool Secure(Func<string, bool> secure, bool desktop) =>
        RuntimeHomeStart.Secure(Home, secure, () => desktop, _shown.Add, _log.Add);
}
